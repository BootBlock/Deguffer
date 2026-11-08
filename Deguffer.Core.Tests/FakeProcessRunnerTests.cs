using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The fake runner's held replies, which let a provider's test prove that its tool queries run at the
/// same time by the order things happened in, never by timing, and the runner's promises that every
/// other test leans on: a synchronous reply, a record of every run, and the cancellation contract.
/// </summary>
public sealed class FakeProcessRunnerTests
{
    private const string Tool = @"C:\Tools\tool.exe";

    /// <summary>Long enough for the runs in these tests to start, short enough to keep a failure quick.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TwoHeldRunsBothStartBeforeEitherReplies()
    {
        var held = new HeldRuns(together: 2, Deadline);
        var runner = new FakeProcessRunner().Responding(Tool, "first", "one").Responding(Tool, "second", "two").Holding(Tool, "query", held);

        var first = runner.RunAsync(Tool, "query first", ToolRun.Query, CancellationToken.None);

        Assert.False(first.IsCompleted, "A held run replied before the second run of its set had started.");

        var second = runner.RunAsync(Tool, "query second", ToolRun.Query, CancellationToken.None);
        var replies = await Task.WhenAll(first, second).WaitAsync(Deadline * 2);

        Assert.Equal(["one", "two"], replies.Select(reply => reply.StandardOutput));
        Assert.True(held.StartedTogether);
        Assert.Equal(2, held.MostAtOnce);
    }

    /// <summary>Awaited one after another, the first held run waits for a second that never starts, and says so.</summary>
    [Fact]
    public async Task AHeldRunWhoseSetNeverStartsFailsAtTheDeadline()
    {
        var held = new HeldRuns(together: 2, TimeSpan.FromMilliseconds(200));
        var runner = new FakeProcessRunner().Holding(Tool, "query", held);

        var failure = await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(Tool, "query", ToolRun.Query, CancellationToken.None));

        Assert.Contains("Only 1 of 2 held runs started", failure.Message, StringComparison.Ordinal);
        Assert.False(held.StartedTogether);
    }

    /// <summary>A run held by a task replies only once that task completes, as the program's replies say.</summary>
    [Fact]
    public async Task ARunHeldByATaskRepliesOnlyOnceItCompletes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeProcessRunner().Responding(Tool, "query", "answer").Holding(Tool, "query", release.Task, Deadline);

        var run = runner.RunAsync(Tool, "query", ToolRun.Query, CancellationToken.None);

        Assert.False(run.IsCompleted, "A run held by a task replied before the task completed.");

        release.SetResult();
        var reply = await run.WaitAsync(Deadline * 2);

        Assert.Equal("answer", reply.StandardOutput);
    }

    /// <summary>A run whose task never completes fails at the deadline, naming the run, rather than hanging.</summary>
    [Fact]
    public async Task ARunHeldByATaskThatNeverCompletesFailsAtTheDeadline()
    {
        var runner = new FakeProcessRunner().Holding(Tool, "query", new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(200));

        var failure = await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(Tool, "query", ToolRun.Query, CancellationToken.None));

        Assert.Contains("'query' was not released within 0.2 s", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A run that arrives once its set has been released replies at once, as an unheld run does.</summary>
    [Fact]
    public async Task ARunAfterTheSetIsReleasedRepliesAtOnce()
    {
        var held = new HeldRuns(together: 1, Deadline);
        var runner = new FakeProcessRunner().Holding(Tool, "query", held);

        await runner.RunAsync(Tool, "query", ToolRun.Query, CancellationToken.None).WaitAsync(Deadline);
        var later = runner.RunAsync(Tool, "query", ToolRun.Query, CancellationToken.None);

        await later.WaitAsync(Deadline);
        Assert.Equal(2, held.Started);
        Assert.Equal(1, held.MostAtOnce);
    }

    /// <summary>
    /// A query cancelled while it is held throws once it is released, as a real tool that is ended on
    /// cancel does, and the run that released it replies as normal.
    /// </summary>
    [Fact]
    public async Task AHeldQueryCancelledBeforeItsReleaseThrowsOnceReleased()
    {
        var held = new HeldRuns(together: 2, Deadline);
        var runner = new FakeProcessRunner().Responding(Tool, "query", "answer").Holding(Tool, "query", held);
        using var cancel = new CancellationTokenSource();

        var cancelled = runner.RunAsync(Tool, "query", ToolRun.Query, cancel.Token);
        await cancel.CancelAsync();

        Assert.False(cancelled.IsCompleted, "A held run ended before its set was released.");

        var released = await runner.RunAsync(Tool, "query", ToolRun.Query, CancellationToken.None).WaitAsync(Deadline);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Deadline));
        Assert.Equal("answer", released.StandardOutput);
    }

    /// <summary>A command that finishes once started keeps its reply after a cancellation, held or not.</summary>
    [Fact]
    public async Task AHeldCommandThatFinishesKeepsItsReplyAfterACancellation()
    {
        var held = new HeldRuns(together: 2, Deadline);
        var runner = new FakeProcessRunner().Responding(Tool, "install", "done").Holding(Tool, "install", held);
        using var cancel = new CancellationTokenSource();
        var finishes = ToolRun.Command(CancelledCommand.Finishes);

        var cancelled = runner.RunAsync(Tool, "install", finishes, cancel.Token);
        await cancel.CancelAsync();
        await runner.RunAsync(Tool, "install", finishes, CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal("done", (await cancelled.WaitAsync(Deadline)).StandardOutput);
    }

    /// <summary>Every existing test relies on these: each reply is given before the call returns, in call order.</summary>
    [Fact]
    public async Task AnUnheldRunRepliesBeforeTheCallReturnsAndIsRecordedInOrder()
    {
        var runner = new FakeProcessRunner().Responding(Tool, "a", "first").Replying(Tool, arguments => arguments == "b" ? new CommandOutcome(0, "second", string.Empty) : null);

        var first = runner.RunAsync(Tool, "a", ToolRun.Query, CancellationToken.None);
        var second = runner.RunAsync(Tool, "b", ToolRun.Query, CancellationToken.None);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal("first", (await first).StandardOutput);
        Assert.Equal("second", (await second).StandardOutput);
        Assert.Equal(["a", "b"], runner.Invocations.Select(invocation => invocation.Arguments));
    }

    /// <summary>Runs started from many threads at once are each recorded, none lost to a race on the record.</summary>
    [Fact]
    public async Task RunsFromManyThreadsAreEachRecorded()
    {
        const int runs = 2_000;
        var runner = new FakeProcessRunner().Responding(Tool, "query", "answer");

        await Parallel.ForAsync(0, runs, async (index, ct) =>
            await runner.RunAsync(Tool, $"query {index}", ToolRun.Query, ct));

        Assert.Equal(
            Enumerable.Range(0, runs).Select(index => $"query {index}").Order(StringComparer.Ordinal),
            runner.Invocations.Select(invocation => invocation.Arguments).Order(StringComparer.Ordinal));
    }
}
