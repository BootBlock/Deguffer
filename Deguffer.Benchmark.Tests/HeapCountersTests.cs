namespace Deguffer.Benchmark.Tests;

/// <summary>
/// What the collector did during a part is the difference of two readings, and the collections of
/// each generation alone are told apart from the runtime's counts, which include the older ones.
/// </summary>
public sealed class HeapCountersTests
{
    [Fact]
    public void ThePartIsWhatChangedBetweenItsStartAndItsEnd()
    {
        var before = new HeapCounters(TimeSpan.FromSeconds(4), Collections: 10, Gen1: 4, Gen2: 1, Allocated: 1_000);
        var after = new HeapCounters(TimeSpan.FromSeconds(4.5), Collections: 17, Gen1: 6, Gen2: 2, Allocated: 9_000);

        Assert.Equal(new HeapCounters(TimeSpan.FromSeconds(0.5), 7, 2, 1, 8_000), after - before);
    }

    /// <summary>
    /// A generation 2 collection counts as one of generation 1 and 0 as well, so of seven collections
    /// with two of generation 1 or older and one of generation 2, five were of generation 0 alone.
    /// </summary>
    [Fact]
    public void EachGenerationAloneLeavesOutTheOlderOnes()
    {
        var counters = new HeapCounters(TimeSpan.Zero, Collections: 7, Gen1: 2, Gen2: 1, Allocated: 0);

        Assert.Equal(5, counters.Gen0Only);
        Assert.Equal(1, counters.Gen1Only);
    }

    /// <summary>A collection in the middle of a part is counted for that part, from the runtime's own counters.</summary>
    [Fact]
    public void ACollectionWhileAPartRunsIsCountedForIt()
    {
        var walks = 0;
        var route = new RaceRoute(
            Route.Walk,
            _ =>
            {
                if (Interlocked.Increment(ref walks) == 4)
                {
                    GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                }

                return new RunTally(1, BytesRead: 0, Complete: true);
            },
            _ => new RunTally(1, BytesRead: 0, Complete: true),
            beforePart: null);

        var race = route.Run(CancellationToken.None).Race!;

        Assert.True(race.WalkRaced.Heap.Collections >= 1);
        Assert.True(race.WalkRaced.Heap.Paused > TimeSpan.Zero);
    }
}
