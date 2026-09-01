using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Conda is the provider whose estimate is not Deguffer's own measurement, so the property that
/// carries the class is that the two numbers stay apart: the figure offered to the user is conda's
/// dry run, which already discounts everything an environment hard-links, and the far larger
/// measurement of the same directories is kept only for the after-run delta.
///
/// Everything runs through <see cref="FakeProcessRunner"/>, so the rules are proved with no conda
/// installed.
/// </summary>
public sealed class CondaCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public CondaCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("conda");
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string RootPrefix => Path.Combine(_environment.UserProfile, "miniconda3");

    private string PackageCache => Path.Combine(RootPrefix, "pkgs");

    private string Environments => Path.Combine(RootPrefix, "envs");

    private static string Quote(string path) => "\"" + path.Replace("\\", "\\\\") + "\"";

    private string InfoJson(params string[] packageCaches)
    {
        var caches = packageCaches.Length > 0 ? packageCaches : [PackageCache];

        return "{ \"root_prefix\": " + Quote(RootPrefix)
            + ", \"pkgs_dirs\": [" + string.Join(", ", caches.Select(Quote)) + "]"
            + ", \"envs_dirs\": [" + Quote(Environments) + "] }";
    }

    /// <summary>
    /// Conda's dry-run report. The trailing line is deliberate: a dry run finishes by raising its
    /// own exit exception, and versions have differed in what reaches stdout afterwards.
    /// </summary>
    private static string CleanJson(long tarballs, long packages) =>
        "{ \"success\": true"
        + ", \"tarballs\": { \"total_size\": " + tarballs + " }"
        + ", \"packages\": { \"total_size\": " + packages + " } }"
        + "\r\nDryRunExit: Dry run. Exiting.";

    private FakeProcessRunner Reporting(
        string? info = null,
        string? clean = null,
        params string[] packageCaches) =>
        new FakeProcessRunner()
            .Responding("info --json", info ?? InfoJson(packageCaches))
            .Responding("--dry-run", clean ?? CleanJson(tarballs: 1_000, packages: 9_000));

    private CondaCacheProvider CreateProvider(FakeProcessRunner? runner = null) =>
        new(_environment, runner ?? Reporting(), FakeProcessInspector.NothingRunning, systemDirectories: _system);

    private static string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    [Fact]
    public async Task ReportsNotPresentWhenCondaIsNotInstalled()
    {
        var provider = new CondaCacheProvider(
            new FakeUserEnvironment(_temp.Path),
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            systemDirectories: _system);

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// §5.1, and the two flags this provider will not pass. <c>--all</c> would take conda's log
    /// files, which §3 places in Tier 3 rather than in this Tier 2 plan, and
    /// <c>--force-pkgs-dirs</c> removes every writable cache whole — conda's own help says it
    /// breaks environments that link by symlink.
    /// </summary>
    [Fact]
    public async Task PlansCondasOwnCommandWithNeitherAllNorForce()
    {
        Populate(PackageCache);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal("clean --index-cache --packages --tarballs --tempfiles --yes", step.Arguments);
        Assert.DoesNotContain("--all", step.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("force-pkgs-dirs", step.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--logfiles", step.Arguments, StringComparison.Ordinal);
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// The central claim. The cache measures 64 KB here because an environment hard-links all of
    /// it; conda's own accounting says 10 KB is unused. Offering the measured figure would promise
    /// six times what the command can free, which is §5.4's over-report on a second subject.
    /// </summary>
    [Fact]
    public async Task TheEstimateIsCondasOwnFigureRatherThanTheMeasuredCache()
    {
        Populate(PackageCache, bytes: 65536);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(10_000, plan.EstimatedBytes);
        Assert.True(plan.Estimated.IsApproximate);
    }

    /// <summary>
    /// The index cache is listed by path in conda's report and never sized, so Deguffer measures
    /// that part itself and adds it. Nothing else in the estimate comes from the filesystem.
    /// </summary>
    [Fact]
    public async Task AddsItsOwnMeasureOfTheIndexCacheWhichCondaDoesNotSize()
    {
        Populate(PackageCache);
        // A whole 4 KB cluster, because this figure is the allocated one: on the file-table
        // route a 2048-byte file reports 4096 and the sum would depend on how Deguffer was
        // launched.
        Populate(Path.Combine(PackageCache, "cache"), bytes: 4096);

        var plan = await CreateProvider().PlanAsync();

        // Logical, not the reclaimable (allocated) figure: allocated rounds to the volume's
        // cluster size, so an exact expectation on it would be a claim about the disk the test
        // happens to run on, and would differ between the two scan routes.
        Assert.Equal(10_000 + 4096, plan.Estimated.Logical);
    }

    /// <summary>
    /// The delta must subtract like from like. The estimate is conda's figure and the executor
    /// re-measures the caches, so without Deguffer's own plan-time probe of those same paths the
    /// reclaim would be computed from two different kinds of number.
    /// </summary>
    [Fact]
    public async Task CarriesDeguffersOwnProbeOfTheCachesForTheAfterRunDelta()
    {
        Populate(PackageCache, bytes: 65536);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(65536, step.MeasuredBefore?.Reclaimable);
        Assert.NotEqual(step.MeasuredBefore?.Reclaimable, step.EstimatedBytes);
    }

    /// <summary>
    /// The delta, driven end to end. The command clears the caches, so the run reclaimed every
    /// byte they held — not the smaller figure conda predicted, and not a negative number from
    /// subtracting a measurement from a prediction.
    /// </summary>
    [Fact]
    public async Task ReportsReclaimAgainstItsOwnProbeRatherThanCondasFigure()
    {
        Populate(PackageCache, bytes: 65536);

        // Stands in for the command actually clearing the cache, so the executor's after-measure
        // has something to subtract.
        var runner = Reporting().Replying(arguments =>
        {
            if (arguments.EndsWith("--yes", StringComparison.Ordinal))
            {
                Directory.Delete(PackageCache, recursive: true);
                return new Safety.CommandOutcome(0, "done", string.Empty);
            }

            return null;
        });

        var provider = CreateProvider(runner);
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.Equal(65536, result.BytesReclaimed);
        Assert.DoesNotContain("grew", Assert.Single(result.Steps).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where conda will not report, the only substitute figure is the wrong one, so the plan
    /// offers nothing and says why rather than falling back to measuring the caches.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhenTheDryRunCannotBeRead()
    {
        Populate(PackageCache, bytes: 65536);

        foreach (var answer in new[] { "", "not json at all", "{ \"success\": false }", "{ \"success\":" })
        {
            var plan = await CreateProvider(Reporting(clean: answer)).PlanAsync();

            Assert.True(plan.IsEmpty);
            Assert.Contains(plan.Notes, n =>
                n.Message.Contains("would count the packages", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task OffersNothingWhenCondaDoesNotSayWhereItsCachesAre()
    {
        var plan = await CreateProvider(Reporting(info: "not json")).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("did not describe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaysSoWhenCondaHasCachedNothingYet()
    {
        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("cached nothing yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaysSoWhenCondaReportsNothingUnused()
    {
        Populate(PackageCache, bytes: 65536);

        var plan = await CreateProvider(Reporting(clean: CleanJson(tarballs: 0, packages: 0))).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("nothing unused", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6. What sits beside a package cache is everything conda installed: the base environment
    /// in the prefix, the user's environments whose packages link back into the cache being
    /// cleaned, and a configuration file that can carry channel tokens.
    /// </summary>
    [Fact]
    public async Task AssertsTheInstallationEnvironmentsAndConfigurationSurvive()
    {
        Populate(PackageCache);
        Populate(Environments);
        File.WriteAllText(Path.Combine(_environment.UserProfile, ".condarc"), "channels: []");

        var plan = await CreateProvider().PlanAsync();

        foreach (var path in (string[])
                 [RootPrefix, Environments, PackageCache, Path.Combine(_environment.UserProfile, ".condarc")])
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheEnvironmentsVanished()
    {
        Populate(PackageCache);
        var environments = Populate(Environments);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad command §5.6 exists to catch.
        Directory.Delete(environments, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c =>
            c.Path.Equals(environments, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Conda reports several writable caches on a machine with more than one installation, and
    /// each is a location the command clears and a directory that must itself survive.
    /// </summary>
    [Fact]
    public async Task CoversEveryPackageCacheCondaReports()
    {
        var second = Path.Combine(_temp.Path, "shared-pkgs");
        Populate(PackageCache);
        Populate(second);

        var plan = await CreateProvider(Reporting(packageCaches: [PackageCache, second])).PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal([PackageCache, second], step.MeasuredPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(second, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A cache conda names but which is not on disk is neither measured nor cleared, so a stale
    /// entry in a configuration file does not produce a step that reclaims nothing.
    /// </summary>
    [Fact]
    public async Task IgnoresAReportedCacheThatIsNotThere()
    {
        Populate(PackageCache);

        var plan = await CreateProvider(
            Reporting(packageCaches: [PackageCache, Path.Combine(_temp.Path, "absent-pkgs")])).PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal([PackageCache], step.MeasuredPaths);
    }

    /// <summary>
    /// Conda's installer does not put it on <c>PATH</c> by default, so an environment variable and
    /// the documented install locations are how it is found on an ordinary machine.
    /// </summary>
    [Fact]
    public async Task FindsCondaThroughCondaExeWhenItIsNotOnPath()
    {
        var exe = Path.Combine(_temp.Path, "elsewhere", "Scripts", "conda.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, []);

        var environment = new FakeUserEnvironment(_temp.Path).WithEnvironmentVariable("CONDA_EXE", exe);
        Populate(Path.Combine(environment.UserProfile, "miniconda3", "pkgs"));

        var provider = new CondaCacheProvider(
            environment, Reporting(), FakeProcessInspector.NothingRunning, systemDirectories: _system);

        Assert.True(await provider.IsPresentAsync());
        Assert.False((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task FindsCondaAtItsDocumentedInstallLocation()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        var exe = Path.Combine(environment.UserProfile, "anaconda3", "Scripts", "conda.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, []);

        var provider = new CondaCacheProvider(
            environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, systemDirectories: _system);

        Assert.True(await provider.IsPresentAsync());
    }

    [Fact]
    public async Task AsksCondaWhereThingsAreOnceAndThenRemembers()
    {
        Populate(PackageCache);
        var runner = Reporting();
        var provider = CreateProvider(runner);

        await provider.IsPresentAsync();
        await provider.PlanAsync();
        await provider.PlanAsync();

        Assert.Single(runner.Invocations, i => i.Arguments == "info --json");
    }

    /// <summary>
    /// A <c>.condarc</c> edit moves the package caches between scans, so a rescan asks again
    /// rather than measuring a directory conda has stopped using.
    /// </summary>
    [Fact]
    public async Task AsksAgainAfterAnInvalidation()
    {
        Populate(PackageCache);
        var runner = Reporting();
        var provider = CreateProvider(runner);

        await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, runner.Invocations.Count(i => i.Arguments == "info --json"));
    }

    /// <summary>
    /// The dry run is asked again on every plan, unlike the locations: what is unused changes
    /// whenever an environment does, so remembering the figure would show a number describing a
    /// machine that has moved on.
    /// </summary>
    [Fact]
    public async Task AsksForTheDryRunOnEveryPlanBecauseTheFigureMoves()
    {
        Populate(PackageCache);
        var runner = Reporting();
        var provider = CreateProvider(runner);

        await provider.PlanAsync();
        await provider.PlanAsync();

        Assert.Equal(2, runner.Invocations.Count(i => i.Arguments.Contains("--dry-run", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WarnsWhenCondaIsRunning()
    {
        Populate(PackageCache);

        var provider = new CondaCacheProvider(
            _environment, Reporting(), new FakeProcessInspector("conda"), systemDirectories: _system);

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>
    /// §6.3. A conda package cache nests by package name and build string and runs deep, so a
    /// MAX_PATH truncation here would silently under-measure the probe the delta is taken from.
    /// </summary>
    [Fact]
    public async Task MeasuresACacheDeeperThanMaxPath()
    {
        var deep = Populate(PackageCache);
        while (deep.Length <= 260)
        {
            deep = Path.Combine(deep, new string('c', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "payload.bin")), new byte[8192]);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(4096 + 8192, step.MeasuredBefore?.Reclaimable);
    }
}
