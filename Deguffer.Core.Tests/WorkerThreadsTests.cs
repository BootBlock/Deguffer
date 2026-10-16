using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The threads a crew's helpers run on: one each at once, the same ones again between runs, none held
/// once the work stops, and none carrying the context of whoever started it.
/// </summary>
public sealed class WorkerThreadsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Every piece of work blocks until all of them are running at once, which only a thread each can
    /// give, and a set of no more than one would never give.
    /// </summary>
    [Fact]
    public void EveryPieceOfWorkRunsOnAThreadOfItsOwnAtOnce()
    {
        const int Pieces = 64;
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);
        using var allRunning = new CountdownEvent(Pieces);
        using var released = new ManualResetEventSlim();
        using var allEnded = new CountdownEvent(Pieces);

        for (var i = 0; i < Pieces; i++)
        {
            set.Run(() =>
            {
                allRunning.Signal();
                released.Wait();
                allEnded.Signal();
            });
        }

        var reached = allRunning.Wait(Patience);
        var running = Pieces - allRunning.CurrentCount;
        released.Set();

        Assert.True(reached, $"{running} of {Pieces} pieces of work were running at once.");
        Assert.True(allEnded.Wait(Patience));
        Assert.Equal(Pieces, set.Count);
    }

    /// <summary>
    /// A parked thread holds nothing of the work it last ran. A file-table pass's work reaches every
    /// array the reader sized to the volume, so a thread holding it while parked would keep a whole
    /// volume's records alive for as long as it waited, after the scan had finished with them.
    /// </summary>
    [Fact]
    public void AParkedThreadHoldsNothingOfItsLastWork()
    {
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);
        using var ran = new ManualResetEventSlim();

        var held = HandOver(set, ran);

        Assert.True(ran.Wait(Patience));
        Assert.True(SpinWait.SpinUntil(() => set.Idle == 1, Patience));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(held.IsAlive, "a parked thread kept its last work's objects alive");
    }

    /// <summary>
    /// Hand <paramref name="set"/> work that holds an object of its own, and return a weak reference
    /// to that object. Apart, so that nothing in the test's own frame holds it.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference HandOver(WorkerThreads set, ManualResetEventSlim ran)
    {
        var payload = new byte[1024];
        set.Run(() =>
        {
            GC.KeepAlive(payload);
            ran.Set();
        });

        return new WeakReference(payload);
    }

    /// <summary>A thread that has finished its work takes the next piece, so a scan starts its threads once.</summary>
    [Fact]
    public void AParkedThreadTakesTheNextPieceOfWork()
    {
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);

        var first = RunAndWait(set, () => Environment.CurrentManagedThreadId);

        // Its work hands back the result just before it parks, and a second piece handed over in
        // that moment would rightly start a second thread.
        Assert.True(SpinWait.SpinUntil(() => set.Idle == 1, Patience));
        var second = RunAndWait(set, () => Environment.CurrentManagedThreadId);

        Assert.Equal(first, second);
        Assert.Equal(1, set.Count);
    }

    /// <summary>
    /// Work handed to a thread in the moment its idle wait gives up still runs. With no idle lifetime
    /// at all, every parked thread is giving up as soon as it parks, so callers handing over work
    /// together keep taking threads in that moment, and a thread that ended regardless would leave a
    /// piece unrun.
    /// </summary>
    [Fact]
    public async Task WorkHandedOverAsTheIdleWaitEndsStillRuns()
    {
        const int Callers = 4;
        const int PiecesEach = 5000;
        var set = new WorkerThreads(TimeSpan.Zero);

        var callers = Enumerable.Range(0, Callers).Select(_ => Task.Factory.StartNew(
            () =>
            {
                using var ran = new SemaphoreSlim(0);

                for (var i = 0; i < PiecesEach; i++)
                {
                    set.Run(() => ran.Release());

                    if (!ran.Wait(Patience))
                    {
                        return i;
                    }
                }

                return PiecesEach;
            },
            TaskCreationOptions.LongRunning));

        var ranEach = await Task.WhenAll(callers);

        Assert.All(ranEach, ran => Assert.Equal(PiecesEach, ran));
    }

    [Fact]
    public void AThreadParkedForItsIdleLifetimeEnds()
    {
        var set = new WorkerThreads(TimeSpan.FromMilliseconds(20));

        RunAndWait(set, () => 0);

        Assert.True(SpinWait.SpinUntil(() => set.Count == 0, Patience), $"{set.Count} threads were still held.");
    }

    /// <summary>
    /// Work runs under no caller's context. A thread started under the first caller's would run every
    /// later piece of work under it too, whoever handed that work over.
    /// </summary>
    [Fact]
    public void WorkDoesNotRunUnderTheContextOfTheCallerThatStartedTheThread()
    {
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);
        var ambient = new AsyncLocal<string?> { Value = "the first caller's" };

        var seen = RunAndWait(set, () => ambient.Value);

        Assert.Null(seen);
    }

    private static T RunAndWait<T>(WorkerThreads set, Func<T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        set.Run(() => result.SetResult(work()));

        Assert.True(result.Task.Wait(Patience));
        return result.Task.Result;
    }
}
