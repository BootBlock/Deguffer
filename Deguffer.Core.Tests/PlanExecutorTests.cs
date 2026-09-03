using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the executor reports a run reclaimed, which is the one number the user checks the tool
/// against. §5.4 names the cost of getting it wrong: "the user will prune, see no change, and lose
/// trust in the tool."
///
/// It had no test file of its own. Seven of the eight providers that emit a command step never call
/// <c>ExecuteAsync</c> in their own tests, so the arithmetic below ran unexamined.
/// </summary>
public sealed class PlanExecutorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A command step's reclaim is the plan-time figure minus a re-measurement of the same paths,
    /// and both readings go through the provider's own scanner. Where the volume index serves them
    /// the second one is not a measurement at all: it is the same pre-command snapshot, because
    /// nothing invalidates the index between planning and executing — <c>Invalidate</c> is called
    /// once, at the top of a planning pass. The two readings cancel and a clean that freed
    /// gigabytes reports nothing.
    ///
    /// <para>Observed on a real volume, elevated, before this test was written: a 10 MB tree
    /// measured through the index at 10,485,760 bytes, deleted, then measured again through the
    /// same scanner at 10,485,760 bytes. Not a rounding difference — the identical figure.</para>
    ///
    /// <para>The fixture serves the table from before the command ran, which is exactly the
    /// snapshot the product holds: the index is built during planning and kept for the life of the
    /// pass. The command here deletes the tree, so a genuine second look answers zero.</para>
    /// </summary>
    [Fact]
    public async Task ReportsWhatACommandFreedRatherThanSubtractingASnapshotFromItself()
    {
        var (cache, fixture) = MirroredTree.Realise(
            _temp,
            new TreeDirectory("cache", new TreeFile("a.bin", 4096), new TreeFile("b.bin", 8192)));

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving(VolumeLetter(cache), fixture));

        var planTime = await scanner.MeasureAsync(cache);
        Assert.Equal(ScanStrategy.MasterFileTable, planTime.Strategy);

        // The step's own command is what empties the tree, as a real cache eviction does.
        var runner = new FakeProcessRunner().Replying(_ =>
        {
            Directory.Delete(cache, recursive: true);
            return new CommandOutcome(0, "cleared", string.Empty);
        });

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps =
            [
                new RunCommandStep("tool", "clean", "Clear the cache with the tool's own command")
                {
                    Estimated = planTime.Size,
                    MeasuredPaths = [cache],
                },
            ],
        };

        var result = await new PlanExecutor(runner, scanner).ExecuteAsync(plan, progress: null, default);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(cache), "the fixture command did not actually empty the tree.");
        Assert.Equal(4096 + 8192, result.BytesReclaimed);
    }

    /// <summary>
    /// A command that freed nothing must report nothing, and the trap is that the two readings come
    /// from different routes.
    ///
    /// <para>The plan-time figure can come from the file table, which knows what a file occupies.
    /// The after-measure comes from a walk, which knows only what a file's length is and says so by
    /// setting allocated equal to logical. Subtracting one from the other compares two different
    /// kinds of byte, and the gap is not academic: cluster slack across a cache of small files makes
    /// allocated much the larger, so a clean that removed nothing would report a reclaim.</para>
    ///
    /// <para>The fixture is a file the table says occupies 8192 bytes and whose length is 4096 — the
    /// ordinary shape of a small file on a 4 KB-cluster volume. Every other mirrored tree in the
    /// suite sets the two equal, which is why nothing here could discriminate before.</para>
    /// </summary>
    [Fact]
    public async Task ReportsNothingWhenTheCommandFreedNothing()
    {
        var (cache, fixture) = MirroredTree.Realise(
            _temp,
            new TreeDirectory("cache", new TreeFile("a.bin", 4096, Allocated: 8192)));

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving(VolumeLetter(cache), fixture));

        var planTime = await scanner.MeasureAsync(cache);
        Assert.Equal(ScanStrategy.MasterFileTable, planTime.Strategy);
        Assert.Equal(8192, planTime.Size.Allocated);
        Assert.Equal(4096, planTime.Size.Logical);

        // A command that succeeds and clears nothing, which is what a failed eviction looks like
        // from here, and what conda's clean looks like for everything an environment still links.
        var runner = new FakeProcessRunner();

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps =
            [
                new RunCommandStep("tool", "clean", "Clear the cache with the tool's own command")
                {
                    Estimated = planTime.Size,
                    MeasuredPaths = [cache],
                },
            ],
        };

        var result = await new PlanExecutor(runner, scanner).ExecuteAsync(plan, progress: null, default);

        Assert.Equal(0, result.BytesReclaimed);
    }

    /// <summary>
    /// The after-measure must not be served from the index, and that is a property of the executor
    /// rather than of any one provider — so it is asserted where every command step passes.
    /// </summary>
    [Fact]
    public async Task TakesTheAfterMeasureFromDiskRatherThanFromACachedIndex()
    {
        var (cache, fixture) = MirroredTree.Realise(
            _temp,
            new TreeDirectory("cache", new TreeFile("a.bin", 4096)));

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving(VolumeLetter(cache), fixture));

        Assert.Equal(ScanStrategy.MasterFileTable, (await scanner.MeasureAsync(cache)).Strategy);

        Directory.Delete(cache, recursive: true);

        // The ordinary route still answers from the snapshot, which is correct for planning and is
        // why the executor cannot use it here.
        Assert.Equal(4096, (await scanner.MeasureAsync(cache)).Size.Reclaimable);
        Assert.Equal(0, (await scanner.MeasureFromDiskAsync(cache)).Size.Reclaimable);
    }

    /// <summary>
    /// A step reports 0 to 1 about itself, and what reaches the caller is that step's slice of the
    /// plan. Getting the offset wrong is not a cosmetic fault: the bar would reach the end while
    /// the first of five steps was still running, and then sit there for the rest of the clean.
    ///
    /// <para>Both steps here finish in one report, so the discriminating value is the first one.
    /// The directory removal's own "done" is 1.0, and it must arrive as 0.5. Neither step carries
    /// an estimate, so this is the equal-share branch of <c>ProgressWeights</c>; the test below
    /// covers the weighting.</para>
    /// </summary>
    [Fact]
    public async Task AStepsOwnFractionArrivesAsItsSliceOfThePlan()
    {
        var directory = _temp.CreateDirectory("cache");
        _temp.CreateFile(64, "cache", "a.bin");
        _temp.CreateFile(64, "cache", "b.bin");
        var file = _temp.CreateFile(64, "dump.dmp");

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps =
            [
                new DeleteDirectoryStep(directory, "A cache"),
                new DeleteFileStep(file, "A dump"),
            ],
        };

        var progress = new ProgressRecorder<double>();

        await new PlanExecutor(new FakeProcessRunner(), new FakeDirectoryScanner())
            .ExecuteAsync(plan, progress, default);

        // Repeats are ordinary — the removal reports its last file and then its own completion —
        // so the claim is about which values appear and in what order, not how many times.
        Assert.Equal([0.5, 1.0], progress.Reports.Distinct());
    }

    /// <summary>
    /// Steps share the bar by what each will free, the same rule the planner applies to whole
    /// plans. One <c>obj</c> of 4 GB beside five of 20 MB is six steps, and an equal split would
    /// crawl through the first sixth of the bar and then jump the rest of it.
    /// </summary>
    [Fact]
    public async Task WeightsTheBarByWhatEachStepFreesRatherThanByHowManyThereAre()
    {
        var big = _temp.CreateDirectory("big");
        _temp.CreateFile(64, "big", "a.bin");
        var small = _temp.CreateDirectory("small");
        _temp.CreateFile(64, "small", "a.bin");

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps =
            [
                new DeleteDirectoryStep(big, "The big one") { Estimated = new ScanSize(9_000, 9_000) },
                new DeleteDirectoryStep(small, "The small one") { Estimated = new ScanSize(1_000, 1_000) },
            ],
        };

        var progress = new ProgressRecorder<double>();

        await new PlanExecutor(new FakeProcessRunner(), new FakeDirectoryScanner())
            .ExecuteAsync(plan, progress, default);

        Assert.Equal([0.9, 1.0], progress.Reports.Select(r => Math.Round(r, 6)).Distinct());
    }

    /// <summary>
    /// A command step reports nothing at all while it runs, so the executor's own report at the end
    /// of each step is the only thing that carries the bar across it.
    ///
    /// <para>The other progress tests here use removals, and a removal reports its own 1.0 on the
    /// way out — which lands on exactly the value the executor would report anyway, and so hides
    /// whether that line ran at all. This is the step type that cannot hide it.</para>
    /// </summary>
    [Fact]
    public async Task ACommandStepAdvancesTheBarThoughItReportsNothingItself()
    {
        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps =
            [
                new RunCommandStep("tool", "clean", "The big one")
                {
                    Estimated = new ScanSize(9_000, 9_000),
                },
                new RunCommandStep("tool", "clean", "The small one")
                {
                    Estimated = new ScanSize(1_000, 1_000),
                },
            ],
        };

        var progress = new ProgressRecorder<double>();

        await new PlanExecutor(new FakeProcessRunner(), new FakeDirectoryScanner())
            .ExecuteAsync(plan, progress, default);

        // Two steps, two reports, and nothing else could have produced either of them.
        Assert.Equal([0.9, 1.0], progress.Reports.Select(r => Math.Round(r, 6)));
    }

    /// <summary>
    /// Every reason must have a sentence or a considered silence, because <c>ScanResult</c> exposes
    /// the lookup as a property and a switch with no arm throws rather than returning nothing. A new
    /// member added without one is a crash on a property access, which is a poor way to find out.
    /// </summary>
    [Fact]
    public void EveryFallbackReasonHasAnAnswerRatherThanAThrow()
    {
        foreach (var reason in Enum.GetValues<FallbackReason>())
        {
            var exception = Record.Exception(() => FallbackReasonText.Describe(reason));

            Assert.True(exception is null, $"{reason} has no arm in FallbackReasonText.Describe.");
        }
    }

    private static char VolumeLetter(string path) => char.ToUpperInvariant(path[0]);

    /// <summary>
    /// A directory whose files are all inside the guard window reclaims nothing and keeps its root,
    /// which is the shape the executor otherwise reads as a step that achieved nothing. It is the
    /// setting working, so it reports success and says what it left — a red row for correct
    /// behaviour would teach the user to distrust the report.
    /// </summary>
    [Fact]
    public async Task ReportsAStepThatKeptEverythingAsSuccessRatherThanFailure()
    {
        var cache = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "written-just-now.bin");

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Keep = MinimumAge.WithinHours(8, DateTime.UtcNow),
            Steps = [new DeleteDirectoryStep(cache, "A cache")],
        };

        var result = await new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default)
            .ExecuteAsync(plan, progress: null, CancellationToken.None);

        var step = Assert.Single(result.Steps);

        Assert.True(step.Succeeded);
        Assert.Equal(1, step.Kept);
        Assert.Equal(0, step.BytesReclaimed);
        Assert.Equal(1, result.KeptCount);
        Assert.Contains("changed too recently", step.Message!, StringComparison.Ordinal);
        Assert.True(Directory.Exists(cache), "the guard kept a file and the folder around it went");
    }

    /// <summary>
    /// The guard travels on the plan, so a plan made without one deletes exactly what it always did.
    /// Reading the setting again at execution is what this rules out: the cut-off would then have
    /// moved, and the clean would take files the preview promised to leave.
    /// </summary>
    [Fact]
    public async Task DeletesEverythingWhenThePlanCarriesNoGuard()
    {
        var cache = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "written-just-now.bin");

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps = [new DeleteDirectoryStep(cache, "A cache")],
        };

        var result = await new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default)
            .ExecuteAsync(plan, progress: null, CancellationToken.None);

        Assert.Equal(0, result.KeptCount);
        Assert.Equal(4096, result.BytesReclaimed);
        Assert.False(Directory.Exists(cache));
    }

    /// <summary>
    /// A directory the guard emptied of candidates is still standing, so the step must not say it
    /// was removed. The success classification is right — Deguffer did what it was asked — but
    /// "Removed" is a claim about the user's disk, and here it is false.
    /// </summary>
    [Fact]
    public async Task DoesNotSayRemovedAboutADirectoryThatIsStillThere()
    {
        var cache = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "written-just-now.bin");

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Keep = MinimumAge.WithinHours(8, DateTime.UtcNow),
            Steps = [new DeleteDirectoryStep(cache, "A cache")],
        };

        var result = await new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default)
            .ExecuteAsync(plan, progress: null, CancellationToken.None);

        var step = Assert.Single(result.Steps);

        Assert.True(Directory.Exists(cache));
        Assert.DoesNotContain("Removed", step.Message!, StringComparison.Ordinal);
        Assert.Contains("changed too recently", step.Message!, StringComparison.Ordinal);
    }
}
