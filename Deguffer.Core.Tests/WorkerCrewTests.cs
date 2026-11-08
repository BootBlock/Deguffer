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
    /// Two crews sharing an allowance have no more helpers at work between them than it holds, however
    /// much their own bounds allow, and each helper gives its place back as it ends.
    /// </summary>
    [Fact]
    public void CrewsSharingAnAllowanceHaveNoMoreHelpersAtWorkThanItHolds()
    {
        const int Places = 5;
        var allowance = new ThreadAllowance(Places);
        using var released = new ManualResetEventSlim();
        using var atWork = new CountdownEvent(Places);

        WorkerCrew Crew() => new(
            64,
            () => () =>
            {
                atWork.Signal();
                released.Wait();
            },
            () => !released.IsSet,
            allowance);

        var first = Crew();
        var second = Crew();

        var started = 0;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            started += (first.TryStartHelper() ? 1 : 0) + (second.TryStartHelper() ? 1 : 0);
        }

        var reached = atWork.Wait(HangGuard);
        released.Set();
        first.Finish();
        second.Finish();

        Assert.True(reached, "The helpers the allowance holds were never all at work.");
        Assert.Equal(Places, started);

        var givenBack = 0;
        while (allowance.TryTake())
        {
            givenBack++;
        }

        Assert.Equal(Places, givenBack);
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
