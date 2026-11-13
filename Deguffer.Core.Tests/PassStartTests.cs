using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The bound on the tool queries the providers start at the top of a planning pass, proved by which
/// queries have started while the others are held open, never by how long anything took.
/// </summary>
public sealed class PassStartTests
{
    /// <summary>Far longer than a started query takes to be seen, and far shorter than a hung test.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Two more queries than the bound, each held open: as many run as the bound allows and no more, and
    /// the next starts only once one of them has ended.
    /// </summary>
    [Fact]
    public async Task NoMoreQueriesRunAtOnceThanTheBoundAllows()
    {
        const int bound = 3;
        using var start = new PassStart(bound, CancellationToken.None);
        var release = new TaskCompletionSource();
        var running = 0;
        var begun = new SemaphoreSlim(0);

        var asked = Enumerable.Range(0, bound + 2).Select(_ => start.Ask(async ct =>
        {
            Interlocked.Increment(ref running);
            begun.Release();

            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Decrement(ref running);
        })).ToList();

        for (var i = 0; i < bound; i++)
        {
            Assert.True(await begun.WaitAsync(Deadline), $"only {i} of {bound} queries started while places were free");
        }

        // Nothing returns a place while every query is held, so a fourth query can only start by
        // taking one the bound does not have.
        Assert.False(await begun.WaitAsync(TimeSpan.FromMilliseconds(200)), "a query started beyond the bound");
        Assert.Equal(bound, Volatile.Read(ref running));

        release.SetResult();
        await Task.WhenAll(asked).WaitAsync(Deadline);

        Assert.Equal(0, running);
    }

    /// <summary>
    /// A pass that ends while a query still waits for a place never starts it, and the work handed back
    /// ends, so the planner's wait for what the pass started does not hang.
    /// </summary>
    [Fact]
    public async Task AQueryStillWaitingWhenThePassEndsNeverStarts()
    {
        using var pass = new CancellationTokenSource();
        using var start = new PassStart(1, pass.Token);
        var release = new TaskCompletionSource();
        var queued = 0;

        var holding = start.Ask(ct => release.Task.WaitAsync(ct));
        var waiting = start.Ask(_ =>
        {
            Interlocked.Increment(ref queued);
            return Task.CompletedTask;
        });

        await pass.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Deadline));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holding.WaitAsync(Deadline));
        Assert.Equal(0, queued);
    }

    /// <summary>
    /// A query's place is given back however it ended, so a tool that failed does not shrink the bound
    /// for every query behind it.
    /// </summary>
    [Fact]
    public async Task AFailedQueryGivesItsPlaceBack()
    {
        using var start = new PassStart(1, CancellationToken.None);

        var failed = start.Ask(_ => throw new InvalidOperationException("The tool could not be started."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(Deadline));

        var next = start.Ask(_ => Task.CompletedTask);
        await next.WaitAsync(Deadline);
    }

    /// <summary>
    /// The query runs off the thread that asked, so a provider's check before its question, a search of
    /// <c>PATH</c> or a probe of a folder, does not hold up the providers the planner asks after it.
    /// </summary>
    [Fact]
    public async Task AskingReturnsBeforeTheProvidersCheckHasRun()
    {
        using var start = new PassStart(1, CancellationToken.None);
        using var check = new ManualResetEventSlim();

        var asked = start.Ask(_ =>
        {
            check.Wait(Deadline);
            return Task.CompletedTask;
        });

        Assert.False(asked.IsCompleted);

        check.Set();
        await asked.WaitAsync(Deadline);
    }
}
