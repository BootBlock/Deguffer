using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// When something that changes on several threads reports its latest state: at once after a quiet
/// spell, otherwise at the end of the interval, and never leaving the last change unsaid.
/// </summary>
public sealed class PacedReportTests
{
    private static readonly TimeSpan Almost = ReportCadence.Interval - TimeSpan.FromMilliseconds(1);

    private readonly ManualTimeProvider _clock = new();
    private readonly List<int> _reports = [];
    private int _state;

    [Fact]
    public void TheFirstChangeIsReportedAtOnce()
    {
        using var paced = Paced();

        Change(1, paced);

        Assert.Equal([1], _reports);
        Assert.Equal(0, _clock.Waiting);
    }

    /// <summary>
    /// Changes made inside the interval are reported once, at its end, and the report says the latest of
    /// them, read when it is made.
    /// </summary>
    [Fact]
    public void ChangesInsideTheIntervalAreReportedOnceAtItsEndAsTheLatest()
    {
        using var paced = Paced();

        Change(1, paced);
        Change(2, paced);
        Change(3, paced);
        Change(4, paced);
        _clock.Advance(Almost);

        Assert.Equal([1], _reports);

        _clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal([1, 4], _reports);
        Assert.Equal(0, _clock.Waiting);
    }

    [Fact]
    public void AChangeAfterAQuietSpellIsReportedAtOnce()
    {
        using var paced = Paced();

        Change(1, paced);
        _clock.Advance(ReportCadence.Interval);
        Change(2, paced);

        Assert.Equal([1, 2], _reports);
        Assert.Equal(0, _clock.Waiting);
    }

    /// <summary>A change every tenth of an interval for ten intervals costs one report per interval, plus the first.</summary>
    [Fact]
    public void ChangesThatKeepComingAreReportedOncePerInterval()
    {
        using var paced = Paced();

        for (var change = 1; change <= 100; change++)
        {
            Change(change, paced);
            _clock.Advance(ReportCadence.Interval / 10);
        }

        Assert.Equal([1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100], _reports);
        Assert.Equal(0, _clock.Waiting);
    }

    [Fact]
    public void NothingIsReportedOnceDisposedATimedReportIncluded()
    {
        var paced = Paced();

        Change(1, paced);
        Change(2, paced);
        paced.Dispose();
        _clock.Advance(ReportCadence.Interval);
        Change(3, paced);
        _clock.Advance(ReportCadence.Interval);

        Assert.Equal([1], _reports);
    }

    private PacedReport Paced() => new(_clock, () => _reports.Add(_state));

    private void Change(int state, PacedReport paced)
    {
        _state = state;
        paced.Changed();
    }
}
