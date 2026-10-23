using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The bounded crew a removal, a refusal check, a transcript read and a duplicate search's reads
/// work through their lists on. Each item blocks its thread on the disk, so the list is worked by
/// its bound from the first item and on no thread of the pool's.
/// </summary>
public sealed class ListWorkersTests
{
    /// <summary>
    /// Past the pool's minimum on any machine of fewer processors, so a run on the pool would reach it
    /// only as the pool grew, about one thread every half second.
    /// </summary>
    private const int Threads = 32;

    private static readonly TimeSpan GatherAtMost = TimeSpan.FromSeconds(10);

    [Fact]
    public void WorksEveryItemOnce()
    {
        var worked = new int[1000];

        ListWorkers.Run(worked.Length, 8, index => Interlocked.Increment(ref worked[index]), default);

        Assert.All(worked, count => Assert.Equal(1, count));
    }

    [Fact]
    public void WorksNothingInAnEmptyList() =>
        ListWorkers.Run(0, 8, _ => Assert.Fail("No item was asked for."), default);

    /// <summary>
    /// Every one of the first items waits until all of them are under way, which only a run at its
    /// bound from the start lets happen before the limit.
    /// </summary>
    [Fact]
    public async Task WorksItsBoundAtOnceAndNoItemOnThePool()
    {
        using var together = new CountdownEvent(Threads);
        var apart = 0;
        var onThePool = 0;

        await WorkerThreads.Shared.RunAsync(
            () =>
            {
                ListWorkers.Run(Threads, Threads, _ =>
                {
                    if (Thread.CurrentThread.IsThreadPoolThread)
                    {
                        Interlocked.Increment(ref onThePool);
                    }

                    together.Signal();

                    if (!together.Wait(GatherAtMost))
                    {
                        Interlocked.Increment(ref apart);
                    }
                }, default);

                return true;
            },
            default);

        Assert.Equal(0, apart);
        Assert.Equal(0, onThePool);
    }

    /// <summary>
    /// The exception an item threw, the same instance and not wrapped, once every thread has ended,
    /// and no item taken after it.
    /// </summary>
    [Fact]
    public void AFailureReachesTheCallerUnchangedAndStopsTheRest()
    {
        var failure = new InvalidOperationException("An invented failure.");
        var worked = 0;

        var thrown = Assert.Throws<InvalidOperationException>(() => ListWorkers.Run(10_000, 4, _ =>
        {
            if (Interlocked.Increment(ref worked) == 1)
            {
                throw failure;
            }

            Thread.Sleep(1);
        }, default));

        Assert.Same(failure, thrown);
        Assert.InRange(worked, 1, 100);
    }

    [Fact]
    public void ACancelledRunStopsTakingItemsAndSaysSo()
    {
        using var cts = new CancellationTokenSource();
        var worked = 0;

        Assert.Throws<OperationCanceledException>(() => ListWorkers.Run(10_000, 4, _ =>
        {
            if (Interlocked.Increment(ref worked) == 1)
            {
                cts.Cancel();
            }

            Thread.Sleep(1);
        }, cts.Token));

        Assert.InRange(worked, 1, 100);
    }

    [Fact]
    public void ARunCancelledBeforeItStartsWorksNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ListWorkers.Run(10, 4, _ => Assert.Fail("No item was taken after the cancellation."), cts.Token));
    }
}
