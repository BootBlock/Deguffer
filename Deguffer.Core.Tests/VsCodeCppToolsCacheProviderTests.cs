using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// cpptools is the second path-based provider, and the one where §5.2's "unrecognised is Tier 4"
/// does real work: every workspace database directory is hex-named, so the recognised set cannot
/// be widened by pattern-matching a name.
/// </summary>
public sealed class VsCodeCppToolsCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public VsCodeCppToolsCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private VsCodeCppToolsCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string CreateCacheRoot()
    {
        var root = Path.Combine(_environment.LocalAppData, "Microsoft", "vscode-cpptools");
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task ReportsNotPresentWhenTheExtensionWasNeverUsed()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    [Fact]
    public async Task PlansThePrecompiledHeaderCacheWithItsMeasuredSize()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 8192);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Path.Combine(root, "ipch")], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > 0);
    }

    [Fact]
    public async Task NeverTargetsTheCppToolsRootDirectory()
    {
        CreateCacheRoot();
        var provider = CreateProvider();
        CreateAt(provider.RootPath, "ipch", 1024);

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(provider.RootPath, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, path => Assert.NotEqual(
            provider.RootPath.TrimEnd(Path.DirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar),
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LeavesWorkspaceDatabasesAloneBecauseAHexNameProvesNothing()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 1024);
        var workspace = CreateAt(root, "0123456789abcdef0123456789abcdef", 65536);

        Assert.Equal(
            SafetyTier.DoNotTouch,
            VsCodeCppToolsCacheProvider.DisposableChildren
                .Classify("0123456789abcdef0123456789abcdef").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(workspace, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, path =>
            Assert.False(IsAtOrUnder(workspace, path), $"{path} would have taken the workspace database with it."));
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("0123456789abcdef0123456789abcdef", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnrecognisedChildIsClassifiedTier4AndLeftAlone()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 1024);
        var unknown = CreateAt(root, "TelemetryProfile", 4096);

        Assert.Equal(
            SafetyTier.DoNotTouch,
            VsCodeCppToolsCacheProvider.DisposableChildren.Classify("TelemetryProfile").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(unknown, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("TelemetryProfile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WarnsWhenTheEditorIsHoldingTheDatabasesOpen()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 1024);

        var provider = new VsCodeCppToolsCacheProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector("Code"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheRootVanished()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 1024);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(root, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecutingRemovesTheHeadersAndLeavesEveryWorkspaceDatabaseStanding()
    {
        var root = CreateCacheRoot();
        CreateAt(root, "ipch", 8192);
        var workspace = CreateAt(root, "fedcba9876543210fedcba9876543210", 4096);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(Path.Combine(root, "ipch")));

        // §5.6: the negative half. The root and the unrecognised sibling both survive.
        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(workspace));
        Assert.True(File.Exists(Path.Combine(workspace, "payload.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task MeasuresPrecompiledHeadersNestedPastMaxPath()
    {
        var root = CreateCacheRoot();
        var ipch = Path.Combine(root, "ipch");

        // §6.3: the extension nests per-workspace header directories, and a MAX_PATH truncation
        // here would be a silent partial deletion.
        //
        // Caveat, established by removing LongPath.Extended and watching this still pass: on a
        // machine with LongPathsEnabled set, .NET accepts these paths without the \\?\ prefix, so
        // this asserts the path is handled but cannot prove the prefix is what handled it. It
        // discriminates only where the registry key is absent — which is the machine that matters.
        var deep = ipch;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "header.ipch")), new byte[4096]);
        Assert.True(deep.Length > 260);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([ipch], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes >= 4096, $"Deep content was not measured: {plan.EstimatedBytes} bytes.");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(LongPath.DirectoryExists(deep));
        Assert.True(Directory.Exists(root));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

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
