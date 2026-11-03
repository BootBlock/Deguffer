using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// A crew has every helper its bound allows at work at once from the start. The walk and the read of
/// the file table both start their helpers here, so this holds for both, where
/// <see cref="WalkThreadCountTests"/> shows it through the walk.
///
/// <para>As there, no assertion depends on how soon the helpers start: every helper is held until all
/// of them are at work, and each says whether it runs on a thread-pool thread, which is what once kept
/// a crew below its bound while the pool grew.</para>
/// </summary>
public sealed class WorkerCrewTests
{
    /// <summary>
    /// How long a crew that never has every helper at work is waited for before the test fails rather
    /// than hangs. Not a measure of the crew: on a loaded machine its threads are scheduled later.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public void EveryHelperTheBoundAllowsIsAtWorkAtOnceOnAThreadOfItsOwn()
    {
        const int Threads = 64;
        const int Helpers = Threads - 1;
        using var allAtWork = new CountdownEvent(Helpers);
        using var released = new ManualResetEventSlim();
        var claimed = 0;
        var onThePool = 0;

        var crew = new WorkerCrew(
            Threads,
            () => () =>
            {
                if (Thread.CurrentThread.IsThreadPoolThread)
                {
                    Interlocked.Increment(ref onThePool);
                }

                allAtWork.Signal();
                released.Wait();
            },
            () => Volatile.Read(ref claimed) < Helpers);

        var started = 0;
        while (crew.TryStartHelper())
        {
            started++;
            Interlocked.Increment(ref claimed);
        }

        var reached = allAtWork.Wait(HangGuard);
        var atWork = Helpers - allAtWork.CurrentCount;
        released.Set();
        crew.Finish();

        Assert.Equal(Helpers, started);
        Assert.True(reached, $"{atWork} of {Helpers} helpers were at work at once, and the crew never had all of them at work.");
        Assert.Equal(0, Volatile.Read(ref onThePool));
    }

    /// <summary>
    /// A helper the system will not start a thread for fails the run rather than throwing where it was
    /// asked for, which may be a read's completion that nothing watches, and is counted out, or the
    /// run would wait for it for ever.
    /// </summary>
    [Fact]
    public async Task AHelperThatCannotBeStartedFailsTheRunAndIsNotWaitedFor()
    {
        var refused = new OutOfMemoryException("No thread could be started.");
        var crew = new WorkerCrew(2, () => static () => { }, () => true, _ => throw refused);

        var started = crew.TryStartHelper();
        var finished = Task.Run(crew.Finish);

        Assert.False(started);
        Assert.True(crew.Failed);
        Assert.Same(refused, await Assert.ThrowsAsync<OutOfMemoryException>(() => finished.WaitAsync(TimeSpan.FromSeconds(10))));
    }
}
