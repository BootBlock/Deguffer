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
}
