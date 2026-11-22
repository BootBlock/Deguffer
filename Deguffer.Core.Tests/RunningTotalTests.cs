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

        running.Report([0], _ => ScanSize.FromLengths(1000));
        running.Report([1], _ => ScanSize.FromLengths(2000));
        running.Report([2], _ => ScanSize.FromLengths(4000));

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

        running.Report([0], _ => ScanSize.FromLengths(1000));
        running.Report([1], _ => ScanSize.FromLengths(500));
        running.Report([0], _ => ScanSize.FromLengths(3000));

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

        running.Report([0], _ => ScanSize.FromLengths(5000));
        running.Report([1], _ => ScanSize.FromLengths(4000));
        running.Report([2], _ => ScanSize.FromLengths(100));

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

        running.Report([0], _ => ScanSize.FromLengths(9000));
        running.Report([1], _ => ScanSize.FromLengths(4000));

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

        running.Report([0], _ => ScanSize.FromLengths(3000));
        running.Report([0], _ => ScanSize.FromLengths(2000));
        running.Report([0], _ => ScanSize.FromLengths(3000));

        Assert.Equal([3000L], progress.Reports.Select(size => size.Reclaimable));
    }

    /// <summary>The paths of a part of the measurement report into the slots of the whole.</summary>
    [Fact]
    public void APartReportsIntoTheSlotsOfTheWhole()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(Asked(@"C:\Users\testuser\a", @"C:\Users\testuser\b", @"C:\Users\testuser\c"), progress);

        var part = running.Of([2, 0]);
        part.Of([1]).Report([0], _ => ScanSize.FromLengths(1000));
        running.Report([2], _ => ScanSize.FromLengths(10));

        // Slot 0 holds 1000, from the part's second path; slot 2 holds 10.
        Assert.Equal(1010, progress.Reports[^1].Reclaimable);
    }

    /// <summary>
    /// A batch of paths reports its sum once, however many paths it holds, so a probe that answers
    /// thousands of files whole sends one figure on rather than one for each file.
    /// </summary>
    [Fact]
    public void ABatchOfPathsIsReportedOnce()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(Asked([.. Enumerable.Range(0, 500).Select(i => $@"C:\Users\testuser\cache\{i}.bin")]), progress);

        running.Report([.. Enumerable.Range(0, 500)], i => ScanSize.FromLengths(i + 1));

        Assert.Equal(500L * 501 / 2, Assert.Single(progress.Reports).Reclaimable);
    }

    /// <summary>
    /// A probe that found only directories answers no path, and says nothing, rather than sending a
    /// zero ahead of the walk's first real figure.
    /// </summary>
    [Fact]
    public void AnEmptyBatchReportsNothing()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(Asked(@"C:\Users\testuser\a"), progress);

        running.Report([], _ => ScanSize.Zero);

        Assert.Empty(progress.Reports);
    }

    /// <summary>
    /// A batch through a part puts each reading in the slot of the whole that its path holds, and asks
    /// for it by the path's index among the part's.
    /// </summary>
    [Fact]
    public void ABatchThroughAPartReportsIntoTheSlotsOfTheWhole()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(Asked(@"C:\Users\testuser\a", @"C:\Users\testuser\b", @"C:\Users\testuser\c"), progress);

        // The part's paths are c, a and b, and its first and third (c and b) are answered.
        running.Of([2, 0, 1]).Report([0, 2], i => ScanSize.FromLengths(i is 0 ? 100 : 20));
        Assert.Equal([120L], progress.Reports.Select(size => size.Reclaimable));

        // c's reading is replaced and a gains one: 7 + 20 + 1.
        running.Report([2, 0], i => ScanSize.FromLengths(i is 2 ? 7 : 1));
        Assert.Equal([120L], progress.Reports.Select(size => size.Reclaimable));

        running.Report([0], _ => ScanSize.FromLengths(200));
        Assert.Equal(227, progress.Reports[^1].Reclaimable);
    }

    /// <summary>
    /// The sum is kept as readings come and go rather than added up again, so it has to come out as
    /// the latest readings add up: every size, the entries, and a flag only while a counted slot still
    /// carries it. Subtracting a <see cref="ScanSize"/> cannot take a flag back out, so a sum kept
    /// that way would stay approximate once any reading was.
    /// </summary>
    [Fact]
    public void TheSumIsThatOfTheLatestReadings()
    {
        var progress = new ProgressRecorder<ScanSize>();
        var running = RunningTotal.Over(
            Asked(@"C:\Users\testuser\a", @"C:\Users\testuser\b", @"C:\Users\testuser\a\inner"),
            progress);

        running.Report([0, 1], i => i is 0 ? new ScanSize(4096, 1000, IsApproximate: true, Entries: 5) : new ScanSize(2000, 2000, Entries: 2, IsCeiling: true));
        Assert.Equal(new ScanSize(6096, 3000, IsApproximate: true, Entries: 7, IsCeiling: true), progress.Reports[^1]);

        // The nested path is left out of the sum, its flag included.
        running.Report([2], _ => ScanSize.Approximate(50_000));

        running.Report([0, 1], i => i is 0 ? new ScanSize(8192, 5000, Entries: 3) : new ScanSize(10, 6000, Entries: 4));
        Assert.Equal(new ScanSize(8202, 11000, Entries: 7), progress.Reports[^1]);
    }

    private static ScanRequest[] Asked(params string[] paths) => [.. paths.Select(path => new ScanRequest(path))];
}
