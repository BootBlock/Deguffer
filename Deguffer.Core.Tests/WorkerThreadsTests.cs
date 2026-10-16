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

    /// <summary>
    /// More passes than the pool's minimum all run at once, each holding its thread until every one is
    /// running. On the pool the ones past the minimum would wait for it to grow, about one every half
    /// second, which for this many is twice the patience given.
    /// </summary>
    [Fact]
    public async Task MorePassesThanThePoolsMinimumRunAtOnce()
    {
        ThreadPool.GetMinThreads(out var poolMinimum, out _);
        var passes = poolMinimum + 40;
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);
        using var allRunning = new CountdownEvent(passes);
        using var released = new ManualResetEventSlim();

        var running = Enumerable.Range(0, passes).Select(_ => set.RunAsync(
            () =>
            {
                allRunning.Signal();
                released.Wait();
                return 0;
            },
            CancellationToken.None)).ToArray();

        var reached = allRunning.Wait(Patience);
        var started = passes - allRunning.CurrentCount;
        released.Set();

        Assert.True(reached, $"{started} of {passes} passes were running at once.");
        await Task.WhenAll(running).WaitAsync(Patience);
    }

    /// <summary>A failure reaches whoever awaits the pass, and never the thread, where it would end the process.</summary>
    [Fact]
    public async Task APassThatFailsFaultsItsTask()
    {
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);

        var pass = set.RunAsync<int>(() => throw new IOException("the volume went away"), CancellationToken.None);

        var failure = await Assert.ThrowsAsync<IOException>(() => pass);
        Assert.Equal("the volume went away", failure.Message);
        Assert.True(pass.IsFaulted);
    }

    /// <summary>A pass stopped for its own token is cancelled, as a caller racing two routes reads it.</summary>
    [Fact]
    public async Task APassStoppedForItsTokenIsCancelledNotFaulted()
    {
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);
        using var stop = new CancellationTokenSource();

        var pass = set.RunAsync<int>(
            () =>
            {
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
                return 0;
            },
            stop.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        Assert.True(pass.IsCanceled);
    }

    /// <summary>
    /// A stop for some other token is the pass's failure, not its cancellation, even with its own token
    /// cancelled as it stops.
    /// </summary>
    [Fact]
    public async Task APassStoppedForAnotherTokenIsFaulted()
    {
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);
        using var own = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        other.Cancel();

        var pass = set.RunAsync<int>(
            () =>
            {
                own.Cancel();
                other.Token.ThrowIfCancellationRequested();
                return 0;
            },
            own.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        Assert.True(pass.IsFaulted);
    }

    [Fact]
    public void APassAskedForWithItsTokenAlreadyCancelledNeverRuns()
    {
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);
        var ran = false;

        var pass = set.RunAsync(() => ran = true, new CancellationToken(canceled: true));

        Assert.True(pass.IsCanceled);
        Assert.False(ran);
        Assert.Equal(0, set.Count);
    }

    /// <summary>
    /// A pass runs under its caller's context, as a task the caller started would: a provider gathers
    /// what its measurements meet in an AsyncLocal. The thread keeps none of it for the next piece of work.
    /// </summary>
    [Fact]
    public async Task APassRunsUnderItsCallersContextAndTheThreadKeepsNone()
    {
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);
        var ambient = new AsyncLocal<string?> { Value = "the caller's" };

        var seen = await set.RunAsync(() => (ambient.Value, Environment.CurrentManagedThreadId), CancellationToken.None);

        Assert.Equal("the caller's", seen.Value);
        Assert.True(SpinWait.SpinUntil(() => set.Idle == 1, Patience));
        var after = RunAndWait(set, () => (ambient.Value, Environment.CurrentManagedThreadId));

        Assert.Equal(seen.CurrentManagedThreadId, after.CurrentManagedThreadId);
        Assert.Null(after.Value);
    }

    /// <summary>
    /// What the caller does next never runs on the pass's thread, even asked to run synchronously, so
    /// the thread goes back to the set as the pass ends rather than carrying the caller's work.
    /// </summary>
    [Fact]
    public async Task WhatFollowsAPassNeverRunsOnItsThread()
    {
        var set = new WorkerThreads(WorkerThreads.IdleLifetime);

        var threads = set.RunAsync(() => Environment.CurrentManagedThreadId, CancellationToken.None)
            .ContinueWith(
                ended => (Pass: ended.Result, Next: Environment.CurrentManagedThreadId),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        var (pass, next) = await threads.WaitAsync(Patience);
        Assert.NotEqual(pass, next);
    }

    private static T RunAndWait<T>(WorkerThreads set, Func<T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        set.Run(() => result.SetResult(work()));

        Assert.True(result.Task.Wait(Patience));
        return result.Task.Result;
    }
}
