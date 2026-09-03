using Deguffer.Core.Execution;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The arithmetic behind a determinate progress bar, and the delivery guarantee underneath it.
/// A bar that goes backwards tells the user the tool has lost track of what it is doing, which on
/// a tool that deletes directories is the last impression it can afford to give.
/// </summary>
public sealed class ScaledProgressTests
{
    [Fact]
    public void MapsAPartsOwnFractionOntoItsSpanOfTheWhole()
    {
        var outer = new ProgressRecorder<double>();
        var scaled = ScaledProgress.Within(outer, offset: 0.5, scale: 0.25)!;

        scaled.Report(0.0);
        scaled.Report(0.5);
        scaled.Report(1.0);

        Assert.Equal([0.5, 0.625, 0.75], outer.Reports);
    }

    /// <summary>
    /// Why this type exists instead of a <see cref="Progress{T}"/>. That one posts each report to
    /// the context captured when it was built, and on a worker there is none — so the reports go to
    /// the thread pool, arrive whenever, and can arrive in the wrong order. Here the outer sees the
    /// value before <c>Report</c> has returned, so the order the caller produced is the order the
    /// bar receives.
    /// </summary>
    [Fact]
    public void ReportsBeforeReturningRatherThanPostingTheValueElsewhere()
    {
        var outer = new ProgressRecorder<double>();

        ScaledProgress.Within(outer, offset: 0, scale: 1)!.Report(0.25);

        Assert.Equal([0.25], outer.Reports);
    }

    /// <summary>
    /// Nobody listening means nothing to wrap, so a caller passes the result straight on without
    /// a null test of its own.
    /// </summary>
    [Fact]
    public void YieldsNothingToReportToWhenTheOuterIsAbsent() =>
        Assert.Null(ScaledProgress.Within(null, offset: 0, scale: 1));
}
