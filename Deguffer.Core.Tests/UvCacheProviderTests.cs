using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// uv is a §5.1 provider, so most of what matters is that it asks the tool rather than assuming.
/// The rest carries §5.6 for the case that makes uv different from npm: the cache sits inside a
/// state directory that also holds installed tools and interpreters.
/// </summary>
public sealed class UvCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public UvCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private UvCacheProvider CreateProvider(FakeProcessRunner? runner = null) =>
        new(_environment, runner ?? new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>Create uv's state directory with a populated cache, and return the cache path.</summary>
    private string CreateCache(long bytes = 4096)
    {
        var cache = Path.Combine(_environment.LocalAppData, "uv", "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "payload.bin"), new byte[bytes]);
        return cache;
    }

    [Fact]
    public async Task ReportsNotPresentWhenUvWasNeverInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    [Fact]
    public async Task AsksUvWhereItsCacheIsRatherThanAssuming()
    {
        _environment.WithExecutable("uv");
        var elsewhere = Path.Combine(_temp.Path, "relocated-cache");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding("cache dir", elsewhere);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(runner.Invocations, i => i.Arguments.Contains("cache dir", StringComparison.Ordinal));
        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(elsewhere));
    }

    [Fact]
    public async Task AsksUvNotToColouriseThePathItIsAboutToBeParsed()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        var runner = new FakeProcessRunner();
        await CreateProvider(runner).PlanAsync();

        // Without this, uv emits ANSI escapes around the path and they land inside it.
        Assert.Contains(runner.Invocations, i =>
            i.Arguments.Contains("--color never", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FallsBackToTheDocumentedLocationWhenUvCannotAnswer()
    {
        _environment.WithExecutable("uv");
        var cache = CreateCache();

        var runner = new FakeProcessRunner().Responding("cache dir", string.Empty, exitCode: 1);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(cache));
    }

    [Fact]
    public async Task EvictsWithUvsOwnCommandRatherThanDeletingThePath()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        var plan = await CreateProvider().PlanAsync();

        // §5.1: nothing is targeted for deletion; the tool is asked to evict.
        Assert.Empty(plan.TargetedPaths);
        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains("cache clean", step.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotPassForceBecauseThatOverridesUvsOwnInUseCheck()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.DoesNotContain("--force", step.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeverTargetsTheUvStateRootBecauseToolsAndInterpretersLiveThere()
    {
        _environment.WithExecutable("uv");
        CreateCache();
        var provider = CreateProvider();

        var tools = Path.Combine(provider.StateRoot, "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllBytes(Path.Combine(tools, "ruff.exe"), new byte[512]);

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(provider.StateRoot, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(
            plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths),
            path => Assert.NotEqual(
                provider.StateRoot.TrimEnd(Path.DirectorySeparatorChar),
                path.TrimEnd(Path.DirectorySeparatorChar),
                StringComparer.OrdinalIgnoreCase));

        // Not merely unmentioned — asserted to survive (§5.6).
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(tools, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(provider.StateRoot, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    [Fact]
    public async Task DoesNotRequireTheCacheDirectoryToSurviveBecauseUvRemovesIt()
    {
        _environment.WithExecutable("uv");
        var cache = CreateCache();

        var plan = await CreateProvider().PlanAsync();

        // 'uv cache clean' deletes the cache root rather than emptying it, and recreates it on next
        // use. Protecting it would fail verification on a successful run.
        Assert.DoesNotContain(plan.ProtectedPaths, p =>
            p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheStateRootVanished()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch: clearing the cache took uv with it.
        Directory.Delete(provider.StateRoot, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c =>
            c.Path.Equals(provider.StateRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReResolvesTheCacheDirectoryAfterInvalidationBecauseItCanMove()
    {
        _environment.WithExecutable("uv");
        var first = CreateCache();

        var moved = Path.Combine(_temp.Path, "moved-cache");
        Directory.CreateDirectory(moved);
        File.WriteAllBytes(Path.Combine(moved, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding("cache dir", first);
        var provider = CreateProvider(runner);

        var before = await provider.PlanAsync();
        Assert.Contains(before.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(first));

        // UV_CACHE_DIR moved between scans; the planner invalidates before replanning.
        runner.Responding("cache dir", moved);
        provider.InvalidateCaches();

        var after = await provider.PlanAsync();

        Assert.Contains(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(moved));
        Assert.DoesNotContain(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(first));
    }

    [Fact]
    public async Task WarnsWhenUvIsRunning()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        var provider = new UvCacheProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector("uv"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task SaysSoWhenUvIsInstalledButHasNeverCachedAnything()
    {
        _environment.WithExecutable("uv");

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("does not exist yet", StringComparison.Ordinal));
    }
}
