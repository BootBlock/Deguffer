using Deguffer.Core.Working;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the taskbar button shows for the work under way on every page. A span left counted after its
/// work ended would keep the bar moving over an idle window for the life of the process, which is
/// the failure the taskbar is most exposed to.
/// </summary>
public sealed class TaskbarIndicatorTests
{
    private readonly BusyWork _work = new();
    private bool _userIsHere = true;

    [Fact]
    public void NothingIsShownWhileNothingIsUnderWay()
    {
        var indicator = Indicator();

        Assert.True(_work.IsIdle);
        Assert.Equal(TaskbarProgress.None, indicator.Current);
    }

    [Fact]
    public void WorkThatCannotSayHowFarItHasGotIsIndeterminateUntilItEnds()
    {
        var indicator = Indicator();

        var span = _work.Begin();

        Assert.False(_work.IsIdle);
        Assert.Equal(TaskbarProgress.Indeterminate, indicator.Current);

        span.Dispose();

        Assert.True(_work.IsIdle);
        Assert.Equal(TaskbarProgress.None, indicator.Current);
    }

    [Fact]
    public void WorkThatSaysHowFarItHasGotFillsTheBar()
    {
        var indicator = Indicator();
        using var span = _work.Begin();

        span.Report(0.25);

        Assert.Equal(new TaskbarProgress(TaskbarState.Normal, 250), indicator.Current);
    }

    /// <summary>Two runs at once read as one, each counting for half of the bar.</summary>
    [Fact]
    public void TwoMeasuredSpansFillTheBarToTheirMean()
    {
        var indicator = Indicator();
        using var scan = _work.Begin();
        using var clean = _work.Begin();

        scan.Report(1);
        clean.Report(0.5);

        Assert.Equal(new TaskbarProgress(TaskbarState.Normal, 750), indicator.Current);
    }

    /// <summary>A fill made from the measured spans alone would claim a measure the whole does not have.</summary>
    [Fact]
    public void OneUnmeasuredSpanMakesTheWholeBarIndeterminate()
    {
        var indicator = Indicator();
        using var scan = _work.Begin();
        using var search = _work.Begin();

        scan.Report(0.5);

        Assert.Equal(TaskbarProgress.Indeterminate, indicator.Current);
    }

    [Fact]
    public void AMeasureCanBeWithdrawn()
    {
        var indicator = Indicator();
        using var clean = _work.Begin();
        clean.Report(1);

        // The re-plan after a clean cannot say how far it has got.
        clean.Report(null);

        Assert.Equal(TaskbarProgress.Indeterminate, indicator.Current);
    }

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(1.5, 1000)]
    [InlineData(double.PositiveInfinity, -1)]
    [InlineData(double.NaN, -1)]
    public void AReportOutsideTheRangeIsHeldToItOrTakenAsUnknown(double reported, int thousandths)
    {
        var indicator = Indicator();
        using var span = _work.Begin();

        span.Report(reported);

        Assert.Equal(
            thousandths < 0 ? TaskbarProgress.Indeterminate : new TaskbarProgress(TaskbarState.Normal, thousandths),
            indicator.Current);
    }

    /// <summary>A write to the taskbar is a call into another process, and a clean reports every 256 files.</summary>
    [Fact]
    public void AReportThatDrawsTheSameBarChangesNothing()
    {
        var indicator = Indicator();
        using var span = _work.Begin();
        span.Report(0.5);
        var changes = 0;
        indicator.Changed += (_, _) => changes++;

        span.Report(0.5001);
        span.Report(0.5002);
        span.Report(0.51);

        Assert.Equal(1, changes);
        Assert.Equal(new TaskbarProgress(TaskbarState.Normal, 510), indicator.Current);
    }

    /// <summary>
    /// A report is posted, so the last ones from a worker arrive after the run has ended. One that
    /// moved its ended span would put a bar back over an idle window.
    /// </summary>
    [Fact]
    public void AReportThatArrivesAfterItsSpanEndedIsIgnored()
    {
        var indicator = Indicator();
        var span = _work.Begin();
        span.Report(0.5);
        span.Dispose();
        var changes = 0;
        _work.Changed += (_, _) => changes++;

        span.Report(0.75);
        span.Fail();

        Assert.Equal(0, changes);
        Assert.Equal(TaskbarProgress.None, indicator.Current);
        Assert.True(_work.IsIdle);
        Assert.Equal(0.5, span.Fraction);
    }

    [Fact]
    public void ASecondDisposalEndsNothingElse()
    {
        var indicator = Indicator();
        var first = _work.Begin();
        using var second = _work.Begin();

        first.Dispose();
        first.Dispose();

        Assert.False(_work.IsIdle);
        Assert.Equal([second], _work.Current);
        Assert.Equal(TaskbarProgress.Indeterminate, indicator.Current);
    }

    /// <summary>The user was in another program, so the red bar is what tells them, and it stays until they look.</summary>
    [Fact]
    public void AFailureWhileTheUserIsAwayLeavesTheBarRedUntilTheWindowIsSeen()
    {
        var indicator = Indicator();
        var span = _work.Begin();
        _userIsHere = false;

        span.Fail();
        span.Dispose();

        Assert.Equal(TaskbarProgress.Error, indicator.Current);

        _userIsHere = true;
        indicator.Seen();

        Assert.Equal(TaskbarProgress.None, indicator.Current);
    }

    /// <summary>
    /// The page shows a failure in front of the user, and a window already in front is not activated
    /// again, so a red bar would have nothing to clear it.
    /// </summary>
    [Fact]
    public void AFailureInFrontOfTheUserLeavesNoRedBar()
    {
        var indicator = Indicator();
        var span = _work.Begin();

        span.Fail();
        span.Dispose();

        Assert.Equal(TaskbarProgress.None, indicator.Current);
    }

    [Fact]
    public void WorkStillUnderWayIsShownOverAnEarlierFailure()
    {
        var indicator = Indicator();
        var failed = _work.Begin();
        var other = _work.Begin();
        other.Report(0.5);
        _userIsHere = false;

        failed.Fail();
        failed.Dispose();

        Assert.Equal(new TaskbarProgress(TaskbarState.Normal, 500), indicator.Current);

        other.Dispose();

        Assert.Equal(TaskbarProgress.Error, indicator.Current);
    }

    [Fact]
    public void TheLastWorkEndingWhileTheUserIsAwayAsksForAttentionOnce()
    {
        var indicator = Indicator();
        var asked = 0;
        indicator.AttentionWanted += (_, _) => asked++;
        var first = _work.Begin();
        var second = _work.Begin();
        _userIsHere = false;

        first.Dispose();

        Assert.Equal(0, asked);

        second.Dispose();
        second.Dispose();

        Assert.Equal(1, asked);
    }

    [Fact]
    public void WorkEndingInFrontOfTheUserAsksForNoAttention()
    {
        var indicator = Indicator();
        var asked = 0;
        indicator.AttentionWanted += (_, _) => asked++;

        _work.Begin().Dispose();

        Assert.Equal(0, asked);
    }

    private TaskbarIndicator Indicator() => new(_work, () => _userIsHere);
}
