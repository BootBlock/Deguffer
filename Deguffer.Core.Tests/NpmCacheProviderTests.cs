using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>§5.1 — npm has an eviction command, so the plan must call it, not delete the folder.</summary>
public sealed class NpmCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public NpmCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>The npm the fake environment resolves, so a test names the program a step must run.</summary>
    private string Npm => _environment.FindExecutable("npm")!;

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
        var runner = new FakeProcessRunner().Responding(Npm, "config get cache", string.Empty);
        var provider = new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        Assert.True(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task PlansTheEvictionCommandRatherThanADirectoryDeletion()
    {
        var plan = await PlanWithPopulatedCache();

        Assert.Empty(plan.TargetedPaths);

        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal(Npm, command.FileName);
        Assert.Equal("cache clean --force", command.Arguments);
        Assert.True(command.EstimatedBytes > 0);
    }

    [Fact]
    public async Task AsksNpmWhereItsCacheIsRatherThanAssuming()
    {
        var relocated = _temp.CreateDirectory("elsewhere", "npm-cache");
        _temp.CreateFile(2048, "elsewhere", "npm-cache", "_cacache", "content", "blob");

        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner().Responding(Npm, "config get cache", relocated);
        var provider = new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        var plan = await provider.PlanAsync();
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal(Npm, command.FileName);
        Assert.Equal([Path.Combine(relocated, "_cacache")], command.MeasuredPaths);
        Assert.True(command.EstimatedBytes >= 2048);
    }

    [Fact]
    public async Task FallsBackToTheDocumentedDefaultWhenNpmDeclinesToAnswer()
    {
        var plan = await PlanWithPopulatedCache(cacheQueryExitCode: 1);
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal([Path.Combine(_environment.LocalAppData, "npm-cache", "_cacache")], command.MeasuredPaths);
    }

    /// <summary>
    /// <c>npm cache clean</c> removes <c>_cacache</c> and leaves <c>_npx</c> and <c>_logs</c> beside
    /// it, so the row offers what the command frees rather than what the folder holds.
    /// </summary>
    [Fact]
    public async Task MeasuresOnlyThePackageCacheNpmsCleanRemoves()
    {
        var cache = Path.Combine(_environment.LocalAppData, "npm-cache");
        WriteFile(8192, cache, "_npx", "0123abcd", "node_modules", "tool.js");
        WriteFile(1024, cache, "_logs", "debug-0.log");

        var plan = await PlanWithPopulatedCache();
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal([Path.Combine(cache, "_cacache")], command.MeasuredPaths);
        Assert.Equal(4096, command.EstimatedBytes);
        Assert.Contains(plan.Notes, n => n.Message.Contains(cache, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadsAsAlreadyClearWhenTheCacheHoldsOnlyWhatNpmsCleanLeaves()
    {
        var cache = Path.Combine(_environment.LocalAppData, "npm-cache");
        WriteFile(8192, cache, "_npx", "0123abcd", "node_modules", "tool.js");
        WriteFile(1024, cache, "_logs", "debug-0.log");

        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner().Responding(Npm, "config get cache", cache);
        var plan = await new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// §5.6: run as npm runs it, the clean takes <c>_cacache</c>, and the verification proves the
    /// cache folder and the packages npx ran outlived it. The logs are not protected, because npm
    /// prunes them itself on every run, so the test checks them directly.
    /// </summary>
    [Fact]
    public async Task VerifiesWhatNpmsCleanLeavesBesideThePackageCacheSurvived()
    {
        var cache = Path.Combine(_environment.LocalAppData, "npm-cache");
        var npx = Path.Combine(cache, "_npx");
        var npxPackage = WriteFile(8192, cache, "_npx", "0123abcd", "node_modules", "tool.js");
        var log = WriteFile(1024, cache, "_logs", "debug-0.log");
        Directory.CreateDirectory(Path.Combine(cache, "_cacache", "content-v2"));
        File.WriteAllBytes(Path.Combine(cache, "_cacache", "content-v2", "blob"), new byte[4096]);

        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner()
            .Responding(Npm, "config get cache", cache)
            .Replying(Npm, arguments =>
            {
                if (arguments != "cache clean --force")
                {
                    return null;
                }

                Directory.Delete(Path.Combine(cache, "_cacache"), recursive: true);
                return new CommandOutcome(0, string.Empty, string.Empty);
            });
        var provider = new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        var plan = await provider.PlanAsync();
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(npx, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);
        var verification = await provider.VerifyAsync(plan);

        Assert.Equal(4096, result.BytesReclaimed);
        Assert.True(verification.Passed);
        Assert.True(File.Exists(npxPackage));
        Assert.True(File.Exists(log));
    }

    [Fact]
    public async Task ProtectsTheAuthTokensInNpmrcAndGloballyInstalledPackages()
    {
        var npmrc = Path.Combine(_environment.UserProfile, ".npmrc");
        File.WriteAllText(npmrc, "//registry.npmjs.org/:_authToken=secret");
        Directory.CreateDirectory(Path.Combine(_environment.RoamingAppData, "npm"));

        var plan = await PlanWithPopulatedCache();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(npmrc, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.EndsWith(Path.Combine("Roaming", "npm"), StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>
    /// §7.1 refuses every path a provider names as protected, so both are refused in Explore too, and
    /// so is what the global packages folder holds.
    /// </summary>
    [Fact]
    public void ExploreRefusesWhatThePlanProtects()
    {
        var provider = new NpmCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());
        var global = Path.Combine(_environment.RoamingAppData, "npm");

        Assert.False(policy.MayRemove(Path.Combine(_environment.UserProfile, ".npmrc")).IsAllowed);
        Assert.False(policy.MayRemove(global).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(global, "node_modules")).IsAllowed);
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

    /// <summary>
    /// A provider given no scanner measures the volumes of the machine it was given. One that measured
    /// this machine's instead would, run elevated, read the real disk's table under the fixture. The
    /// environment's tables count what they were asked, so no real volume can stand in for them.
    /// </summary>
    [Fact]
    public async Task WithNoScannerGivenTheVolumesMeasuredAreTheEnvironments()
    {
        var tables = FakeMftSourceFactory.Unavailable(FallbackReason.NotNtfsVolume);
        _environment.VolumeTables = tables;

        var plan = await PlanWithPopulatedCache();

        Assert.True(tables.OpenCount > 0, "The plan measured without asking the environment's tables.");
        Assert.Equal(FallbackReason.NotNtfsVolume, plan.Fallback);
    }

    [Fact]
    public async Task ReResolvesTheCacheDirectoryAfterInvalidationBecauseItCanMove()
    {
        // Both paths are deliberately away from DefaultCacheDirectory, so the first assertion
        // proves npm was asked rather than passing on the fallback by coincidence.
        var first = _temp.CreateDirectory("configured", "npm-cache");
        _temp.CreateFile(4096, "configured", "npm-cache", "_cacache", "payload.bin");

        var moved = _temp.CreateDirectory("elsewhere", "npm-cache");
        _temp.CreateFile(2048, "elsewhere", "npm-cache", "_cacache", "payload.bin");

        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner().Responding(Npm, "config get cache", first);
        var provider = new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        var before = await provider.PlanAsync();
        Assert.Contains(before.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(Path.Combine(first, "_cacache")));

        // npm's cache config moved between scans; the planner invalidates before replanning.
        runner.Responding(Npm, "config get cache", moved);
        provider.InvalidateCaches();

        var after = await provider.PlanAsync();

        Assert.Contains(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(Path.Combine(moved, "_cacache")));
        Assert.DoesNotContain(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(Path.Combine(first, "_cacache")));
    }

    [Fact]
    public async Task CleanedPlacesCoverTheDefaultCacheTheCommandIsSentTo()
    {
        var cache = Path.Combine(_environment.LocalAppData, "npm-cache", "_cacache", "content-v2");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "blob"), new byte[4096]);

        // npm declines to answer, so the plan sends its command to the documented default.
        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner().Responding(Npm, "config get cache", string.Empty, exitCode: 1);
        var provider = new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(provider));
    }

    [Fact]
    public async Task CleanedPlacesCoverTheCacheNpmReportsElsewhere()
    {
        var relocated = _temp.CreateDirectory("elsewhere", "npm-cache");
        _temp.CreateFile(2048, "elsewhere", "npm-cache", "_cacache", "content", "blob");

        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner().Responding(Npm, "config get cache", relocated);
        var provider = new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(provider));
    }

    private static string WriteFile(int bytes, string folder, params string[] segments)
    {
        var path = Path.Combine([folder, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private async Task<CleanupPlan> PlanWithPopulatedCache(
        int cacheQueryExitCode = 0,
        IDirectoryScanner? scanner = null)
    {
        var cache = Path.Combine(_environment.LocalAppData, "npm-cache");
        Directory.CreateDirectory(Path.Combine(cache, "_cacache", "content-v2"));
        File.WriteAllBytes(Path.Combine(cache, "_cacache", "content-v2", "blob"), new byte[4096]);

        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner().Responding(Npm, "config get cache", cache, cacheQueryExitCode);

        return await new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning, scanner)
            .PlanAsync();
    }
}
