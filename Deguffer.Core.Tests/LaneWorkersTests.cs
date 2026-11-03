using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The lanes a duplicate search reads its files in: each worked by its own bound, none waiting for
/// another, the highest priority first, and every lane stopped by a failure in one.
/// </summary>
public sealed class LaneWorkersTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Work queued from one lane's work in another is worked there, and the run ends only once it
    /// has: each item on lane 0 queues one on lane 1, which queues one more on lane 0.
    /// </summary>
    [Fact]
    public async Task WorkQueuedInAnotherLaneIsWorkedBeforeTheRunEnds()
    {
        var workers = new LaneWorkers<(int Step, int Index)>([2, 2], default);
        var worked = new int[3, 50];

        await workers.RunAsync(
            Enumerable.Range(0, 50).Select(i => (Lane: 0, Item: (Step: 0, Index: i), Priority: 0L)),
            item =>
            {
                Interlocked.Increment(ref worked[item.Step, item.Index]);

                if (item.Step < 2)
                {
                    workers.Add((item.Step + 1) % 2, (item.Step + 1, item.Index), 0);
                }
            }).WaitAsync(Patience);

        Assert.All(worked.Cast<int>(), count => Assert.Equal(1, count));
    }

    /// <summary>
    /// A lane takes its highest priority first, and of two the same, the one queued first, whenever
    /// the higher one arrives. One thread, so the order is exact.
    /// </summary>
    [Fact]
    public async Task ALaneTakesItsHighestPriorityFirst()
    {
        var workers = new LaneWorkers<string>([1], default);
        List<string> order = [];

        await workers.RunAsync(
            [(0, "low", 1), (0, "first of the middle", 5), (0, "second of the middle", 5)],
            item =>
            {
                order.Add(item);

                if (item == "first of the middle")
                {
                    workers.Add(0, "high, queued late", 9);
                }
            }).WaitAsync(Patience);

        Assert.Equal(["first of the middle", "high, queued late", "second of the middle", "low"], order);
    }

    /// <summary>
    /// A lane is worked by its own bound and no more, even where every item it is given arrives from
    /// another lane's work. Each item on lane 1 holds its thread for a while, so a second thread
    /// there would overlap it.
    /// </summary>
    [Fact]
    public async Task ALaneOfOneIsNeverWorkedByTwoHoweverItsWorkArrives()
    {
        var workers = new LaneWorkers<int>([4, 1], default);
        var running = 0;
        var peak = 0;

        await workers.RunAsync(
            Enumerable.Range(0, 20).Select(i => (Lane: 0, Item: i, Priority: 0L)),
            item =>
            {
                if (item >= 0)
                {
                    workers.Add(1, -1, 0);
                    return;
                }

                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                Thread.Sleep(5);
                Interlocked.Decrement(ref running);
            }).WaitAsync(Patience);

        Assert.Equal(1, peak);
    }

    /// <summary>
    /// A failure in one lane stops every lane and is thrown unchanged. The other lane has emptied its
    /// queue and waits for more, which it would wait for forever if the failure did not stop it.
    /// </summary>
    [Fact]
    public async Task AFailureInOneLaneStopsEveryLaneAndIsThrownUnchanged()
    {
        var workers = new LaneWorkers<int>([1, 1], default);

        var failure = await Assert.ThrowsAsync<IOException>(() => workers.RunAsync(
            [(0, 0, 0), (1, 1, 0)],
            item =>
            {
                if (item == 1)
                {
                    Thread.Sleep(50);
                    throw new IOException("the disk went away");
                }
            }).WaitAsync(Patience));

        Assert.Equal("the disk went away", failure.Message);
    }

    /// <summary>
    /// A stop ends every lane, starts no item after it, and is thrown as a cancellation. Lane 0 holds
    /// every item, on one thread, so no item can be under way there as the stop comes. Lane 1 has
    /// nothing and waits for more, which it would wait for forever if the stop did not end it.
    /// </summary>
    [Fact]
    public async Task AStopEndsEveryLaneAndStartsNoMoreItems()
    {
        using var stop = new CancellationTokenSource();
        var workers = new LaneWorkers<int>([1, 1], stop.Token);
        var startedAfterTheStop = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workers.RunAsync(
            Enumerable.Range(0, 100).Select(i => (Lane: 0, Item: i, Priority: 0L)),
            item =>
            {
                if (stop.IsCancellationRequested)
                {
                    Interlocked.Increment(ref startedAfterTheStop);
                }

                if (item == 10)
                {
                    stop.Cancel();
                }
            }).WaitAsync(Patience));

        Assert.Equal(0, startedAfterTheStop);
    }

    [Fact]
    public async Task ARunGivenNothingEndsAtOnce()
    {
        var workers = new LaneWorkers<int>([1, 4], default);

        await workers.RunAsync([], _ => throw new InvalidOperationException("nothing was queued")).WaitAsync(Patience);
    }

    private static void InterlockedMax(ref int peak, int value)
    {
        for (var seen = Volatile.Read(ref peak); value > seen; seen = Volatile.Read(ref peak))
        {
            Interlocked.CompareExchange(ref peak, value, seen);
        }
    }
}
