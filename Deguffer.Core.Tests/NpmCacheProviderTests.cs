using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>§5.1 — npm has an eviction command, so the plan must call it, not delete the folder.</summary>
public sealed class NpmCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public NpmCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ReportsNotPresentWhenNpmIsNotInstalled()
    {
        var provider = new NpmCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task ReportsNothingToDoWhenTheCacheDirectoryHasNotBeenCreatedYet()
    {
        _environment.WithExecutable("npm");
        var provider = new NpmCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

        Assert.True(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task PlansTheEvictionCommandRatherThanADirectoryDeletion()
    {
        var plan = await PlanWithPopulatedCache();

        Assert.Empty(plan.TargetedPaths);

        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal("cache clean --force", command.Arguments);
        Assert.True(command.EstimatedBytes > 0);
    }

    [Fact]
    public async Task AsksNpmWhereItsCacheIsRatherThanAssuming()
    {
        var relocated = _temp.CreateDirectory("elsewhere", "npm-cache");
        _temp.CreateFile(2048, "elsewhere", "npm-cache", "_cacache", "content", "blob");

        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner().Responding("config get cache", relocated);
        var provider = new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        var plan = await provider.PlanAsync();
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal([relocated], command.MeasuredPaths);
        Assert.True(command.EstimatedBytes >= 2048);
    }

    [Fact]
    public async Task FallsBackToTheDocumentedDefaultWhenNpmDeclinesToAnswer()
    {
        var plan = await PlanWithPopulatedCache(cacheQueryExitCode: 1);
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal([Path.Combine(_environment.LocalAppData, "npm-cache")], command.MeasuredPaths);
    }

    [Fact]
    public async Task ProtectsTheAuthTokensInNpmrcAndGloballyInstalledPackages()
    {
        var npmrc = Path.Combine(_environment.UserProfile, ".npmrc");
        File.WriteAllText(npmrc, "//registry.npmjs.org/:_authToken=secret");
        Directory.CreateDirectory(Path.Combine(_environment.RoamingAppData, "npm"));

        var plan = await PlanWithPopulatedCache();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(npmrc, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.EndsWith(Path.Combine("Roaming", "npm"), StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    [Fact]
    public async Task VerificationAcknowledgesPathsThatWereNeverThere()
    {
        // A path that did not exist before cannot be evidence of survival, and the report says so
        // rather than quietly counting it as a pass.
        var plan = await PlanWithPopulatedCache();
        var provider = new NpmCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

        var verification = await provider.VerifyAsync(plan);

        Assert.True(verification.Passed);
        Assert.Contains(verification.Checks, c => c.Detail.Contains("Not present before", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.5: the route reaches the plan as data, not only as a sentence in the notes. The UI decides
    /// whether to offer elevation from this, so a provider that measured its paths and dropped the
    /// reason on the floor would silently withdraw the offer with a green test suite.
    /// </summary>
    [Fact]
    public async Task CarriesTheScanRouteOntoThePlanAndNotOnlyIntoItsNotes()
    {
        var plan = await PlanWithPopulatedCache(
            scanner: new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated)));

        Assert.Equal(FallbackReason.NotElevated, plan.Fallback);
        Assert.Contains(plan.Notes, n => n.Message.Contains("administrator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReResolvesTheCacheDirectoryAfterInvalidationBecauseItCanMove()
    {
        // Both paths are deliberately away from DefaultCacheDirectory, so the first assertion
        // proves npm was asked rather than passing on the fallback by coincidence.
        var first = _temp.CreateDirectory("configured", "npm-cache");
        File.WriteAllBytes(Path.Combine(first, "payload.bin"), new byte[4096]);

        var moved = _temp.CreateDirectory("elsewhere", "npm-cache");
        File.WriteAllBytes(Path.Combine(moved, "payload.bin"), new byte[2048]);

        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner().Responding("config get cache", first);
        var provider = new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        var before = await provider.PlanAsync();
        Assert.Contains(before.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(first));

        // npm's cache config moved between scans; the planner invalidates before replanning.
        runner.Responding("config get cache", moved);
        provider.InvalidateCaches();

        var after = await provider.PlanAsync();

        Assert.Contains(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(moved));
        Assert.DoesNotContain(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(first));
    }

    private async Task<CleanupPlan> PlanWithPopulatedCache(
        int cacheQueryExitCode = 0,
        IDirectoryScanner? scanner = null)
    {
        var cache = Path.Combine(_environment.LocalAppData, "npm-cache");
        Directory.CreateDirectory(Path.Combine(cache, "_cacache", "content-v2"));
        File.WriteAllBytes(Path.Combine(cache, "_cacache", "content-v2", "blob"), new byte[4096]);

        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner().Responding("config get cache", cache, cacheQueryExitCode);

        return await new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning, scanner)
            .PlanAsync();
    }
}
