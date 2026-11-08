using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The running figure of a measurement of several paths (§5.5): every path's latest reading added up,
/// a nested path left out, and a sum that never falls.
/// </summary>
public class RunningTotalTests
{
    [Fact]
    public void ReportsTheSumOfEveryPath()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(Asked(@"C:\Users\testuser\a", @"C:\Users\testuser\b", @"C:\Users\testuser\c"), progress);

        running.Report(0, ScanSize.FromLengths(1000));
        running.Report(1, ScanSize.FromLengths(2000));
        running.Report(2, ScanSize.FromLengths(4000));

        Assert.Equal([1000L, 3000L, 7000L], progress.Reports.Select(size => size.Reclaimable));
    }

    /// <summary>
    /// A path read twice, by a walk and then by the table, holds one slot, so its second reading
    /// replaces its first rather than adding to it.
    /// </summary>
    [Fact]
    public void ALaterReadingOfAPathReplacesItsEarlierOne()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(Asked(@"C:\Users\testuser\a", @"C:\Users\testuser\b"), progress);

        running.Report(0, ScanSize.FromLengths(1000));
        running.Report(1, ScanSize.FromLengths(500));
        running.Report(0, ScanSize.FromLengths(3000));

        Assert.Equal(3500, progress.Reports[^1].Reclaimable);
    }

    /// <summary>
    /// A path inside another counts its files towards the outer path as well, so it stays out of the
    /// sum. Compared ignoring case, as the outer path's walk may spell it either way.
    /// </summary>
    [Fact]
    public void APathInsideAnotherIsNotCountedTwice()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(
            Asked(@"C:\Users\testuser\cache", @"C:\USERS\TESTUSER\CACHE\inner", @"C:\Users\testuser\cache two"),
            progress);

        running.Report(0, ScanSize.FromLengths(5000));
        running.Report(1, ScanSize.FromLengths(4000));
        running.Report(2, ScanSize.FromLengths(100));

        Assert.Equal(5100, progress.Reports[^1].Reclaimable);
    }

    /// <summary>
    /// A volume's root keeps its separator, so the prefix below it must not gain a second one, which
    /// nothing inside the volume would start with.
    /// </summary>
    [Fact]
    public void APathInsideAVolumeRootIsNotCountedTwice()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(Asked(@"C:\", @"C:\Users\testuser\cache"), progress);

        running.Report(0, ScanSize.FromLengths(9000));
        running.Report(1, ScanSize.FromLengths(4000));

        Assert.Equal([9000L], progress.Reports.Select(size => size.Reclaimable));
    }

    [Fact]
    public void APathAskedAboutTwiceIsCountedOnce()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(Asked(@"C:\Users\testuser\a", @"C:\Users\testuser\a\"), progress);

        running.Report(2, _ => ScanSize.FromLengths(1000));

        Assert.Equal(1000, Assert.Single(progress.Reports).Reclaimable);
    }

    /// <summary>
    /// The table's answer for a path can come in under what a raced walk had already counted of it. A
    /// figure that went down would read as bytes uncounted, so the lower sum is not reported.
    /// </summary>
    [Fact]
    public void TheSumNeverFalls()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(Asked(@"C:\Users\testuser\a"), progress);

        running.Report(0, ScanSize.FromLengths(3000));
        running.Report(0, ScanSize.FromLengths(2000));
        running.Report(0, ScanSize.FromLengths(3000));

        Assert.Equal([3000L], progress.Reports.Select(size => size.Reclaimable));
    }

    /// <summary>The paths of a part of the measurement report into the slots of the whole.</summary>
    [Fact]
    public void APartReportsIntoTheSlotsOfTheWhole()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(Asked(@"C:\Users\testuser\a", @"C:\Users\testuser\b", @"C:\Users\testuser\c"), progress);

        var part = running.Of([2, 0]);
        part.Of([1]).Report(0, ScanSize.FromLengths(1000));
        running.Report(2, ScanSize.FromLengths(10));

        // Slot 0 holds 1000, from the part's second path; slot 2 holds 10.
        Assert.Equal(1010, progress.Reports[^1].Reclaimable);
    }

    private static ScanRequest[] Asked(params string[] paths) => [.. paths.Select(path => new ScanRequest(path))];
}
