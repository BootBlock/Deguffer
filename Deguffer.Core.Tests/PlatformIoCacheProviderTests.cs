using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// PlatformIO is the first Tier 2 provider, so alongside the §5.1 and §5.6 rules these cover the
/// thing that makes it Tier 2 at all: the tier travels into the plan, where §7's confirmation is
/// derived from it.
///
/// The subject is a core directory whose disposable cache is a small fraction of its size, sitting
/// beside gigabytes of installed toolchain. That is precisely the shape a size-driven rule gets
/// catastrophically wrong, so the negative assertions carry most of the weight here.
/// </summary>
public sealed class PlatformIoCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public PlatformIoCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private PlatformIoCacheProvider CreateProvider(FakeProcessRunner? runner = null) =>
        new(_environment, runner ?? new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string CoreRoot => Path.Combine(_environment.UserProfile, ".platformio");

    /// <summary>The cache in its default place, populated.</summary>
    private string CreateCache(long bytes = 4096)
    {
        var cache = Path.Combine(CoreRoot, ".cache");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "payload.bin"), new byte[bytes]);
        return cache;
    }

    /// <summary>The expensive siblings: installed toolchains, the interpreter, and user libraries.</summary>
    private string[] CreateInstalledToolchains()
    {
        string[] siblings =
        [
            Path.Combine(CoreRoot, "packages"),
            Path.Combine(CoreRoot, "platforms"),
            Path.Combine(CoreRoot, "penv"),
            Path.Combine(CoreRoot, "python3"),
            Path.Combine(CoreRoot, "lib"),
        ];

        foreach (var sibling in siblings)
        {
            Directory.CreateDirectory(sibling);
            File.WriteAllBytes(Path.Combine(sibling, "payload.bin"), new byte[8192]);
        }

        return siblings;
    }

    /// <summary>
    /// What <c>pio system prune --core-packages --platform-packages --dry-run</c> printed on the
    /// surveyed machine: two installed <c>espressif32</c> versions between them referenced every
    /// tool package, so nothing was reclaimable.
    /// </summary>
    private const string NothingUnnecessary =
        """
        Dry run mode (do not prune, only show data that will be removed)

        Prune unnecessary core packages:
        Calculating...
        Space on disk: 0B

        Prune unnecessary development platform packages:
        Calculating...
        Space on disk: 0B

        Total reclaimed space: 0B
        """;

    /// <summary>
    /// The same report from a machine that upgraded a platform in place, leaving the superseded
    /// toolchain behind — the case the package row exists for.
    /// </summary>
    private const string ASupersededToolchain =
        """
        Dry run mode (do not prune, only show data that will be removed)

        Prune unnecessary core packages:
        Calculating...
        Space on disk: 0B

        Prune unnecessary development platform packages:
        Calculating...
        Package                                     Version              Size
        ------------------------------------------  -------------------  ---------
        platformio/toolchain-xtensa-esp32 @ ~8.4.0  8.4.0+2021r2-patch5  256.34MB
        Space on disk: 256.34MB

        Total reclaimed space: 256.34MB
        """;

    /// <summary>256.34 × 1,048,576, which is what PlatformIO's humanised total can mean.</summary>
    private const long SupersededToolchainBytes = 268_791_972;

    /// <summary>A runner that answers the package dry run and nothing else.</summary>
    private static FakeProcessRunner Reporting(string pruneReport) =>
        new FakeProcessRunner().Responding("--dry-run", pruneReport);

    private static RunCommandStep StepContaining(CleanupPlan plan, string argument) =>
        Assert.Single(
            plan.Steps.OfType<RunCommandStep>(),
            step => step.Arguments.Contains(argument, StringComparison.Ordinal));

    private static string InfoJson(string? coreDir = null, string? cacheDir = null)
    {
        var fields = new Dictionary<string, object>();

        if (coreDir is not null)
        {
            fields["core_dir"] = coreDir;
        }

        if (cacheDir is not null)
        {
            fields["cache_dir"] = cacheDir;
        }

        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public async Task ReportsNotPresentWhenPlatformIoWasNeverInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>The tier is the product (§3), and §7 derives the confirmation from it.</summary>
    [Fact]
    public async Task IsTier2AndCarriesThatIntoThePlanAndItsConfirmation()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);

        var plan = await provider.PlanAsync();
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);

        // Tier 2 is offered but never pre-selected, and needs a deliberate yes before it runs.
        Assert.False(plan.Tier.IsPreSelectedByDefault());
        Assert.Equal(ConfirmationLevel.Acknowledgement, ConfirmationRequirement.For(plan).Level);
    }

    [Fact]
    public async Task AsksPlatformIoWhereItsCacheIsRatherThanAssuming()
    {
        _environment.WithExecutable("pio");
        var elsewhere = Path.Combine(_temp.Path, "relocated-cache");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding("system info", InfoJson(cacheDir: elsewhere));
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(runner.Invocations, i =>
            i.Arguments.Contains("--json-output", StringComparison.Ordinal));
        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(elsewhere));
    }

    /// <summary>
    /// PLATFORMIO_CORE_DIR relocates the whole core directory, and the cache goes with it even when
    /// cache_dir itself is not reported.
    /// </summary>
    [Fact]
    public async Task DerivesTheCacheFromARelocatedCoreDirectoryWhenOnlyThatIsReported()
    {
        _environment.WithExecutable("pio");
        var relocatedCore = Path.Combine(_temp.Path, "elsewhere", ".platformio");
        var relocatedCache = Path.Combine(relocatedCore, ".cache");
        Directory.CreateDirectory(relocatedCache);
        File.WriteAllBytes(Path.Combine(relocatedCache, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding("system info", InfoJson(coreDir: relocatedCore));
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(relocatedCache));
    }

    /// <summary>Some versions wrap each value as {"value": …, "default": …}.</summary>
    [Fact]
    public async Task ReadsTheWrappedValueShapeAsWellAsThePlainOne()
    {
        _environment.WithExecutable("pio");
        var elsewhere = Path.Combine(_temp.Path, "wrapped-cache");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "payload.bin"), new byte[2048]);

        var json =
            $$$"""{"cache_dir": {"value": {{{JsonSerializer.Serialize(elsewhere)}}}, "default": null}}""";
        var runner = new FakeProcessRunner().Responding("system info", json);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(elsewhere));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Usage: pio system info [OPTIONS]")] // an older build that ignores --json-output
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("{\"core_dir\": null}")]
    [InlineData("{\"cache_dir\": \"not-a-rooted-path\"}")]
    public async Task FallsBackToTheDocumentedLocationWhenPlatformIoCannotAnswer(string output)
    {
        _environment.WithExecutable("pio");
        var cache = CreateCache();

        var runner = new FakeProcessRunner().Responding("system info", output);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(cache));
    }

    [Fact]
    public async Task EvictsWithPlatformIosOwnCommandRatherThanDeletingThePath()
    {
        _environment.WithExecutable("pio");
        CreateCache();

        var plan = await CreateProvider().PlanAsync();

        // §5.1: nothing is targeted for deletion; the tool is asked to evict.
        Assert.Empty(plan.TargetedPaths);
        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains("system prune", step.Arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scoping flag is the safety property, and it survives the packages being offered too. An
    /// unscoped prune does the cache and the packages under one description, so the user would be
    /// agreeing to a toolchain removal by agreeing to clear a cache. Two flags, two rows, two
    /// decisions.
    /// </summary>
    [Fact]
    public async Task ScopesTheCacheStepToTheCacheEvenWhenPackagesAreOfferedBesideIt()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        CreateInstalledToolchains();

        var plan = await CreateProvider(Reporting(ASupersededToolchain)).PlanAsync();

        var step = StepContaining(plan, "--cache");
        Assert.DoesNotContain("--core-packages", step.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--platform-packages", step.Arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.1 for the half of this directory that holds the gigabytes: PlatformIO is asked what its
    /// own prune would remove, and that answer is the offer.
    /// </summary>
    [Fact]
    public async Task OffersWhatPlatformIoReportsAsUnnecessaryAsASecondStep()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        CreateInstalledToolchains();
        var provider = CreateProvider(Reporting(ASupersededToolchain));

        var plan = await provider.PlanAsync();

        var step = StepContaining(plan, "--core-packages");
        Assert.Contains("--platform-packages", step.Arguments, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(provider.CoreRoot, "packages"), step.MeasuredPaths);

        // The figure is PlatformIO's own, rounded to two decimal places before Deguffer sees it —
        // never a measurement of the packages directory, which holds far more than this.
        Assert.Equal(SupersededToolchainBytes, step.EstimatedBytes);
        Assert.True(step.Estimated.IsApproximate);
    }

    /// <summary>
    /// The two figures are different kinds of number and must stay apart. Deguffer's probe counts
    /// every toolchain in <c>packages</c>, most of which stays; PlatformIO's estimate counts only
    /// what it would remove. The executor subtracts its after-measure from the probe, so pairing it
    /// with the estimate instead would report a reclaim of minus the whole directory.
    /// </summary>
    [Fact]
    public async Task CarriesDeguffersOwnProbeSeparatelyFromPlatformIosEstimate()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        CreateInstalledToolchains();

        var plan = await CreateProvider(Reporting(ASupersededToolchain)).PlanAsync();

        var step = StepContaining(plan, "--core-packages");
        Assert.NotNull(step.MeasuredBefore);

        var probed = step.MeasuredBefore.Value;
        Assert.True(probed.Reclaimable > 0, "The packages directory was not probed at all.");
        Assert.NotEqual(step.Estimated.Reclaimable, probed.Reclaimable);
    }

    /// <summary>
    /// The command that asks must not be able to remove anything. <c>--dry-run</c> is read-only in
    /// PlatformIO's source, and <c>--force</c> is deliberately absent from it: were the dry-run flag
    /// ever lost from that string, what is left prompts and aborts rather than pruning in silence.
    /// </summary>
    [Fact]
    public async Task AsksWithADryRunThatWouldStopAndAskIfItEverStoppedBeingOne()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        var runner = Reporting(ASupersededToolchain);

        await CreateProvider(runner).PlanAsync();

        var asked = Assert.Single(runner.Invocations, i =>
            i.Arguments.Contains("--core-packages", StringComparison.Ordinal));

        Assert.Contains("--dry-run", asked.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", asked.Arguments, StringComparison.Ordinal);
    }

    /// <summary>The evidence behind a multi-gigabyte row: PlatformIO's own list, named.</summary>
    [Fact]
    public async Task NamesThePackagesPlatformIoFlagged()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        CreateInstalledToolchains();

        var plan = await CreateProvider(Reporting(ASupersededToolchain)).PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("platformio/toolchain-xtensa-esp32 @ ~8.4.0", StringComparison.Ordinal)
            && n.Message.Contains("256.34MB", StringComparison.Ordinal));
    }

    /// <summary>
    /// The surveyed machine's own answer. Two installed platform versions referenced every tool
    /// package, so the honest offer is no row at all — and the packages directory is not even
    /// measured, because there is nothing for the measurement to inform.
    /// </summary>
    [Fact]
    public async Task OffersNoPackageRowWherePlatformIoReportsNothingUnnecessary()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        CreateInstalledToolchains();
        var provider = CreateProvider(Reporting(NothingUnnecessary));

        var plan = await provider.PlanAsync();

        Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.DoesNotContain(
            Path.Combine(provider.CoreRoot, "packages"),
            plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths));

        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("nothing unnecessary", StringComparison.Ordinal));
    }

    /// <summary>
    /// A report Deguffer cannot read is not a zero, and the difference matters: the only substitute
    /// figure available is a measurement of <c>packages</c>, which counts the toolchains every
    /// installed platform still needs. Nothing is offered, and the user is told why.
    /// </summary>
    [Fact]
    public async Task OffersNoPackageRowWhenPlatformIoWillNotSayWhatItWouldRemove()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        CreateInstalledToolchains();
        var provider = CreateProvider(Reporting("Usage: pio system prune [OPTIONS]"));

        var plan = await provider.PlanAsync();

        Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.DoesNotContain(
            Path.Combine(provider.CoreRoot, "packages"),
            plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths));

        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("did not report", StringComparison.Ordinal));
    }

    /// <summary>
    /// The two halves are independent subjects, and the larger one must not depend on the smaller
    /// one existing. A machine that has never populated its cache can still hold a superseded
    /// toolchain, and that is the whole of what this provider is for.
    /// </summary>
    [Fact]
    public async Task StillOffersUnusedPackagesWhenTheCacheDirectoryDoesNotExist()
    {
        _environment.WithExecutable("pio");
        CreateInstalledToolchains();

        var plan = await CreateProvider(Reporting(ASupersededToolchain)).PlanAsync();

        var step = StepContaining(plan, "--core-packages");
        Assert.Equal(SupersededToolchainBytes, step.EstimatedBytes);
        Assert.DoesNotContain(plan.Steps.OfType<RunCommandStep>(), s =>
            s.Arguments.Contains("--cache", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.1 and §5.6 with the package row in play, which is when the negative is worth most: the
    /// step reaches inside <c>packages</c>, so every sibling and the folder itself are asserted to
    /// survive rather than merely left unmentioned.
    /// </summary>
    [Fact]
    public async Task TargetsNoPathAndProtectsTheToolchainsEvenWhenPackagesAreOffered()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        var siblings = CreateInstalledToolchains();
        var provider = CreateProvider(Reporting(ASupersededToolchain));

        var plan = await provider.PlanAsync();

        Assert.Equal(2, plan.Steps.OfType<RunCommandStep>().Count());
        Assert.Empty(plan.TargetedPaths);

        foreach (var sibling in siblings.Append(provider.CoreRoot))
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    /// <summary>
    /// PLATFORMIO_CORE_DIR moves the whole core directory, and §5.6's assertions have to move with
    /// it. Built from the default profile location instead, every protected path would be absent and
    /// recorded as never present — six checks that pass while establishing nothing, on exactly the
    /// installs where the guess about where PlatformIO lives has already been shown wrong.
    /// </summary>
    [Fact]
    public async Task ProtectsTheCoreDirectoryPlatformIoReportedRatherThanTheDefaultOne()
    {
        _environment.WithExecutable("pio");
        var relocated = Path.Combine(_temp.Path, "elsewhere", ".platformio");

        string[] siblings = ["packages", "platforms", "penv", "python3", "lib"];
        foreach (var sibling in siblings)
        {
            Directory.CreateDirectory(Path.Combine(relocated, sibling));
        }

        var runner = new FakeProcessRunner()
            .Responding("system info", InfoJson(coreDir: relocated))
            .Responding("--dry-run", ASupersededToolchain);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(relocated, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        foreach (var sibling in siblings)
        {
            var path = Path.Combine(relocated, sibling);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        Assert.Contains(
            Path.Combine(relocated, "packages"),
            StepContaining(plan, "--core-packages").MeasuredPaths);
    }

    /// <summary>
    /// §5.6, and the case this provider exists to get right: the toolchains are most of the core
    /// directory's size, and none of them may be touched to reclaim the cache beside them.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheCoreRootOrTheInstalledToolchainsBesideTheCache()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        var siblings = CreateInstalledToolchains();
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(provider.CoreRoot, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        var measured = plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths).ToList();

        foreach (var sibling in siblings.Append(provider.CoreRoot))
        {
            Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(measured, path => Assert.NotEqual(
                sibling.TrimEnd(Path.DirectorySeparatorChar),
                path.TrimEnd(Path.DirectorySeparatorChar),
                StringComparer.OrdinalIgnoreCase));

            // Not merely unmentioned — asserted to survive (§5.6).
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheInstalledPackagesVanished()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        CreateInstalledToolchains();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch: the prune took the toolchains with it.
        var packages = Path.Combine(provider.CoreRoot, "packages");
        Directory.Delete(packages, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c =>
            c.Path.Equals(packages, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReResolvesTheCacheDirectoryAfterInvalidationBecauseItCanMove()
    {
        _environment.WithExecutable("pio");
        var first = CreateCache();

        var moved = Path.Combine(_temp.Path, "moved-cache");
        Directory.CreateDirectory(moved);
        File.WriteAllBytes(Path.Combine(moved, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding("system info", InfoJson(cacheDir: first));
        var provider = CreateProvider(runner);

        var before = await provider.PlanAsync();
        Assert.Contains(before.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(first));

        runner.Responding("system info", InfoJson(cacheDir: moved));
        provider.InvalidateCaches();

        var after = await provider.PlanAsync();

        Assert.Contains(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(moved));
        Assert.DoesNotContain(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(first));
    }

    [Fact]
    public async Task WarnsWhenPlatformIoIsRunning()
    {
        _environment.WithExecutable("pio");
        CreateCache();

        var provider = new PlatformIoCacheProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector("pio"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task SaysSoWhenPlatformIoIsInstalledButHasNeverCachedAnything()
    {
        _environment.WithExecutable("pio");

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("does not exist yet", StringComparison.Ordinal));
    }

    /// <summary>
    /// PlatformIO reports its own cache location, so a cache relocated past <c>MAX_PATH</c> must
    /// still be measured rather than skipped.
    ///
    /// The caveat is not the machine's <c>LongPathsEnabled</c> setting, as this once said. The
    /// runtime prepends <c>\\?\</c> at 260 characters regardless, so an assertion on the outcome of
    /// a filesystem operation cannot fail anywhere — see
    /// <see cref="LongPathTests.TheRuntimeStillReachesPastMaxPathWithoutOurPrefix"/>.
    /// </summary>
    [Fact]
    public async Task MeasuresACacheRelocatedBeyondMaxPath()
    {
        _environment.WithExecutable("pio");

        var deep = _temp.Path;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('p', 40));
        }

        var cache = Path.Combine(deep, ".cache");
        Assert.True(cache.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(cache));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(cache, "payload.bin")), new byte[4096]);

        var runner = new FakeProcessRunner().Responding("system info", InfoJson(cacheDir: cache));
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(cache));
        Assert.True(plan.EstimatedBytes > 0, "A cache past MAX_PATH was measured as empty.");
    }
}
