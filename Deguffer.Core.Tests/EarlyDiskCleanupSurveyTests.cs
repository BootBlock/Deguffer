using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A Disk Cleanup handler asked before the plan knows it needs the answer. A handler told to stop
/// answers "has something", so what these hold is that a stopped survey's answer is never read, and
/// that no survey is left running.
/// </summary>
public sealed class EarlyDiskCleanupSurveyTests : IDisposable
{
    private const string Handler = "Device Driver Packages";
    private const string Volume = @"C:\";

    private static readonly TimeSpan Hung = TimeSpan.FromSeconds(10);

    /// <summary>A measurement that never finishes on its own, released when the test ends.</summary>
    private readonly TaskCompletionSource _endless = new();

    public void Dispose() => _endless.TrySetResult();

    [Fact]
    public async Task TakesTheHandlersAnswer()
    {
        var handlers = FakeDiskCleanupHandlers.FindingNothing();
        var survey = new EarlyDiskCleanupSurvey(handlers, Handler, Volume, CancellationToken.None);

        var answer = await survey.TakeAsync().WaitAsync(Hung);

        Assert.Equal(DiskCleanupAnswer.NothingToClear, answer.Answer);
        Assert.Equal([(Handler, false)], handlers.Returned);
    }

    [Fact]
    public async Task DiscardingStopsTheHandlerAndWaitsForItToReturn()
    {
        var handlers = FakeDiskCleanupHandlers.Measuring(_endless.Task);
        var survey = new EarlyDiskCleanupSurvey(handlers, Handler, Volume, CancellationToken.None);
        await handlers.SurveyStarted.WaitAsync(Hung);

        await survey.DiscardAsync().WaitAsync(Hung);

        Assert.Equal([(Handler, true)], handlers.Returned);
    }

    /// <summary>
    /// The pass cancelled while the handler measured: it answers "has something" on being stopped, and
    /// that answer must not reach a plan as though Windows had given it.
    /// </summary>
    [Fact]
    public async Task RefusesAnAnswerGivenAfterThePassWasCancelled()
    {
        using var pass = new CancellationTokenSource();
        var handlers = FakeDiskCleanupHandlers.Measuring(_endless.Task, DiskCleanupAnswer.NothingToClear);
        var survey = new EarlyDiskCleanupSurvey(handlers, Handler, Volume, pass.Token);
        await handlers.SurveyStarted.WaitAsync(Hung);

        await pass.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => survey.TakeAsync().WaitAsync(Hung));
        Assert.Equal([(Handler, true)], handlers.Returned);
    }

    [Fact]
    public async Task DiscardingAnAnswerAlreadyTakenDoesNothing()
    {
        var handlers = FakeDiskCleanupHandlers.FindingNothing();
        var survey = new EarlyDiskCleanupSurvey(handlers, Handler, Volume, CancellationToken.None);
        await survey.TakeAsync().WaitAsync(Hung);

        await survey.DiscardAsync().WaitAsync(Hung);

        Assert.Equal([(Handler, false)], handlers.Returned);
    }

    [Fact]
    public async Task ADiscardedSurveyCannotBeTaken()
    {
        var handlers = FakeDiskCleanupHandlers.Measuring(_endless.Task);
        var survey = new EarlyDiskCleanupSurvey(handlers, Handler, Volume, CancellationToken.None);

        await survey.DiscardAsync().WaitAsync(Hung);

        await Assert.ThrowsAsync<InvalidOperationException>(survey.TakeAsync);
    }

    [Fact]
    public async Task AbandoningStopsTheHandler()
    {
        var handlers = FakeDiskCleanupHandlers.Measuring(_endless.Task);
        var survey = new EarlyDiskCleanupSurvey(handlers, Handler, Volume, CancellationToken.None);
        await handlers.SurveyStarted.WaitAsync(Hung);

        survey.Abandon();

        await handlers.SurveyReturned.WaitAsync(Hung);
        Assert.Equal([(Handler, true)], handlers.Returned);
    }
}
