using Deguffer.Core.Viewing;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reports from any thread reach the thread that made the progress in batches: at most one batch
/// waiting at a time, a batch no more often than the interval while reports pour in, none longer than
/// the budget, every report in the order made, and the end awaited until the last is handed over.
/// </summary>
public sealed class BatchedProgressTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly QueuedContext _thread = new();
    private readonly List<int> _handed = [];
    private int _batches;

    private BatchedProgress<int> Progress(Action<int>? each = null)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_thread);

        try
        {
            return new BatchedProgress<int>(
                _time,
                value =>
                {
                    _handed.Add(value);
                    each?.Invoke(value);
                },
                () => _batches++);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>The first report is posted at once, and every report made while it waits joins it.</summary>
    [Fact]
    public void ReportsMadeWhileABatchWaitsJoinIt()
    {
        using var progress = Progress();

        for (var i = 0; i < 1000; i++)
        {
            progress.Report(i);
        }

        Assert.Equal(1, _thread.Waiting);

        _thread.RunPending();

        Assert.Equal(Enumerable.Range(0, 1000), _handed);
        Assert.Equal(1, _thread.Ran);
        Assert.Equal(1, _batches);
    }

    /// <summary>
    /// A report soon after a batch waits for the interval since that batch started, so a search
    /// confirming groups faster than the page could show each costs a few posts a second.
    /// </summary>
    [Fact]
    public void AReportSoonAfterABatchWaitsForTheInterval()
    {
        using var progress = Progress();
        progress.Report(1);
        _thread.RunPending();

        progress.Report(2);
        progress.Report(3);

        Assert.Equal(0, _thread.Waiting);
        Assert.Equal(1, _time.Waiting);

        _time.Advance(BatchedProgress<int>.Interval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, _thread.Waiting);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, _thread.Waiting);

        _thread.RunPending();
        Assert.Equal([1, 2, 3], _handed);
        Assert.Equal(2, _batches);
    }

    /// <summary>A report after a quiet spell longer than the interval is posted at once.</summary>
    [Fact]
    public void AReportAfterAQuietSpellIsPostedAtOnce()
    {
        using var progress = Progress();
        progress.Report(1);
        _thread.RunPending();
        _time.Advance(BatchedProgress<int>.Interval);

        progress.Report(2);

        Assert.Equal(1, _thread.Waiting);
        Assert.Equal(0, _time.Waiting);
    }

    /// <summary>
    /// A batch stops once it has run for the budget, and what it left is posted again at once, so a
    /// backlog is handed over in turns of about a frame with the thread free between them.
    /// </summary>
    [Fact]
    public void ABatchStopsAtTheBudgetAndPostsTheRestAtOnce()
    {
        var step = TimeSpan.FromMilliseconds(1);
        var perBatch = (int)(BatchedProgress<int>.Budget / step);
        using var progress = Progress(_ => _time.Advance(step));

        for (var i = 0; i < 100; i++)
        {
            progress.Report(i);
        }

        _thread.RunOne();

        Assert.Equal(perBatch, _handed.Count);
        Assert.Equal(1, _thread.Waiting);

        _thread.RunPending();

        Assert.Equal(Enumerable.Range(0, 100), _handed);
        Assert.Equal((100 + perBatch - 1) / perBatch, _batches);
    }

    /// <summary>
    /// The end posts what still waits for the interval at once, and answers only once it is handed
    /// over, so the code that says a search ended runs after its last result is shown.
    /// </summary>
    [Fact]
    public void TheEndAnswersOnceTheLastReportIsHandedOver()
    {
        using var progress = Progress();
        progress.Report(1);
        _thread.RunPending();
        progress.Report(2);

        var ended = progress.EndAsync();

        Assert.False(ended.IsCompleted);
        Assert.Equal(1, _thread.Waiting);

        _thread.RunPending();

        Assert.True(ended.IsCompletedSuccessfully);
        Assert.Equal([1, 2], _handed);

        // The wait the interval armed was dropped when the end posted its batch.
        _time.Advance(BatchedProgress<int>.Interval);
        Assert.Equal(0, _thread.Waiting);
    }

    /// <summary>Nothing waiting, the end answers at once, and a report after it is a broken contract.</summary>
    [Fact]
    public void TheEndOfNothingAnswersAtOnceAndNoReportFollowsIt()
    {
        using var progress = Progress();

        Assert.True(progress.EndAsync().IsCompletedSuccessfully);
        Assert.Throws<InvalidOperationException>(() => progress.Report(1));
    }
}
