using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The progress of a removal that deletes while it is still listing: a fraction of what the plan
/// measured until the walk has counted the tree, then of what the walk counted, never backwards, and
/// reported by the clock rather than by how many entries have gone.
/// </summary>
public sealed class RemovalProgressTests
{
    /// <summary>The least time between two reports.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly ManualTimeProvider _clock = new();
    private readonly List<double> _reported = [];

    [Fact]
    public void IsAFractionOfThePlanWhileTheWalkIsStillCounting()
    {
        var progress = Progress(planned: 4);

        progress.Found(2);
        _clock.Advance(Interval);
        progress.Done();

        Assert.Equal([0.25], _reported);
    }

    [Fact]
    public void IsAFractionOfWhatTheWalkCountedOnceItHasCountedTheTree()
    {
        var progress = Progress(planned: 4);

        progress.Found(2);
        progress.Counted();
        _clock.Advance(Interval);
        progress.Done();

        Assert.Equal([0.5], _reported);
    }

    /// <summary>
    /// The plan measured less than the walk then found, so the fraction a report would give falls.
    /// The bar is held where it was instead.
    /// </summary>
    [Fact]
    public void NeverMovesBackwardsWhenTheWalkFindsMoreThanThePlan()
    {
        var progress = Progress(planned: 2);

        progress.Found(1);
        _clock.Advance(Interval);
        progress.Done();
        progress.Found(4);
        _clock.Advance(Interval);
        progress.Done();
        _clock.Advance(Interval);
        progress.Done();
        progress.Finished();

        Assert.Equal([0.5, 0.6, 1.0], _reported);
    }

    /// <summary>
    /// However many entries a fast volume deletes before the clock moves, the UI thread is posted only
    /// the end, not one report for every few hundred entries.
    /// </summary>
    [Fact]
    public void AManyEntryRemovalInNoTimeReportsOnlyItsEnd()
    {
        const int entries = 10_000;
        var progress = Progress(planned: entries);

        progress.Found(entries);
        progress.Counted();
        for (var entry = 0; entry < entries; entry++)
        {
            progress.Done();
        }

        progress.Finished();

        Assert.Equal([1.0], _reported);
    }

    [Fact]
    public void ReportsOncePerIntervalAsTheClockMoves()
    {
        var progress = Progress(planned: 8);
        var almost = Interval - TimeSpan.FromMilliseconds(1);

        _clock.Advance(almost);
        progress.Done();
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        progress.Done();
        _clock.Advance(almost);
        progress.Done();
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        progress.Done();
        progress.Finished();

        Assert.Equal([0.25, 0.5, 1.0], _reported);
    }

    /// <summary>The crew deletes on several threads, and only one of those that find a report due makes it.</summary>
    [Fact]
    public void ThreadsThatFindAReportDueTogetherMakeItOnce()
    {
        const int entries = 100_000;
        var progress = Progress(planned: entries);

        _clock.Advance(Interval);
        Parallel.For(0, entries, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => progress.Done());
        progress.Finished();

        Assert.Equal(2, _reported.Count);
        Assert.Equal(1.0, _reported[^1]);
    }

    private RemovalProgress Progress(long planned) =>
        new(new CallbackProgress<double>(_reported.Add), planned, _clock);
}
