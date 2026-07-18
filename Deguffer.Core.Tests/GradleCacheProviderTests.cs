using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Gradle is the path-based provider, so it carries the load for §5.2. These are mostly negative
/// tests: what must never appear in a plan matters more than what does.
/// </summary>
public sealed class GradleCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public GradleCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private GradleCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string CreateGradleHome()
    {
        var root = Path.Combine(_environment.UserProfile, ".gradle");
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task ReportsNotPresentWhenGradleWasNeverInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    [Fact]
    public async Task PlansCachesAndWrapperWithTheirMeasuredSizes()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 4096);
        CreateAt(root, "wrapper", 2048);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            [Path.Combine(root, "caches"), Path.Combine(root, "wrapper")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(plan.EstimatedBytes > 0);
    }

    [Fact]
    public async Task NeverTargetsTheGradleRootDirectory()
    {
        CreateGradleHome();
        var provider = CreateProvider();
        CreateAt(provider.RootPath, "caches", 1024);

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(provider.RootPath, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, path => Assert.NotEqual(
            provider.RootPath.TrimEnd(Path.DirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar),
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NeverPlansGradlePropertiesBecauseItMayHoldSigningKeys()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 1024);
        var properties = Path.Combine(root, "gradle.properties");
        File.WriteAllText(properties, "signing.keyId=DEADBEEF");

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(properties, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, path =>
            Assert.False(IsAtOrUnder(properties, path), $"{path} would have taken gradle.properties with it."));

        // It is not merely absent from the plan — it is asserted to survive (§5.6).
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(properties, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    [Fact]
    public async Task UnrecognisedChildIsClassifiedTier4AndLeftAlone()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 1024);
        var unknown = CreateAt(root, "daemon", 8192);

        Assert.Equal(SafetyTier.DoNotTouch, GradleCacheProvider.DisposableChildren.Classify("daemon").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(unknown, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("daemon", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WarnsWhenAGradleProcessIsHoldingTheCacheOpen()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 1024);

        var provider = new GradleCacheProvider(_environment, new FakeProcessRunner(), new FakeProcessInspector("java"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfAProtectedPathVanished()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 1024);
        var properties = Path.Combine(root, "gradle.properties");
        File.WriteAllText(properties, "org.gradle.jvmargs=-Xmx2g");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch.
        File.Delete(properties);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(properties, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecutingRemovesTheCachesAndLeavesConfigStanding()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 4096);
        CreateAt(root, "wrapper", 4096);
        var properties = Path.Combine(root, "gradle.properties");
        File.WriteAllText(properties, "org.gradle.caching=true");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(Path.Combine(root, "caches")));
        Assert.False(Directory.Exists(Path.Combine(root, "wrapper")));

        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(properties));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>Create <paramref name="child"/> under the root holding one file of the given size.</summary>
    private static string CreateAt(string root, string child, int bytes)
    {
        var directory = Path.Combine(root, child);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
