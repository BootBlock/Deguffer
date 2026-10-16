using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// What the Explore page tells the taskbar button: its scan, with the measure the scan reports, and
/// its removal, each ended however it ends.
/// </summary>
public sealed class ExploreWorkTests : IDisposable
{
    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    [Fact]
    public void AScanSaysHowFarItHasGotAndEndsWithIt() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var scanning = page.ScanCommand.ExecuteAsync(null);
        var scan = _explore.Scanner.Current;

        scan.Report(new ExploreProgress(Done: 5, Total: 10, BytesSeen: 0));
        await Task.Yield();

        Assert.Equal(0.5, Assert.Single(_explore.Work.Current).Fraction);

        scan.Finish(ExploreScan.Fast(new ExploreTreeBuilder(@"C:\").Build(ExploreChildOrder.BySize)));
        await scanning;

        Assert.True(_explore.Work.IsIdle);
    });

    [Fact]
    public void AScanThatCannotReadTheDriveEndsItsWorkAndSaysItFailed() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var failures = 0;
        _explore.Work.Failed += (_, _) => failures++;
        var scanning = page.ScanCommand.ExecuteAsync(null);

        _explore.Scanner.Current.Fail(new IOException("The device is not ready."));
        await scanning;

        Assert.True(_explore.Work.IsIdle);
        Assert.Equal(1, failures);
    });

    [Fact]
    public void ACancelledScanEndsItsWorkWithoutAFailure() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var failures = 0;
        _explore.Work.Failed += (_, _) => failures++;
        var scanning = page.ScanCommand.ExecuteAsync(null);

        _explore.Scanner.Current.Fail(new OperationCanceledException());
        await scanning;

        Assert.True(_explore.Work.IsIdle);
        Assert.Equal(0, failures);
    });

    [Fact]
    public void ARemovalIsUnderWayUntilItHasReported() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(Path.GetPathRoot(_explore.Temp.Path)!);
        var underWay = false;
        _explore.Bin = new FakeRecycleBin(path =>
        {
            underWay = !_explore.Work.IsIdle;
            Directory.Delete(path, recursive: true);

            return new RecycleOutcome(Removed: true);
        });
        var page = _explore.Page();
        var (tree, folder) = _explore.OnDisk();
        page.ScopeTo(tree.PathOf(tree.RootNode));
        await _explore.ScanAsync(page, ExploreScan.Fast(tree));
        var failures = 0;
        _explore.Work.Failed += (_, _) => failures++;

        page.Selection.Select([folder]);
        await page.Selection.DeleteCommand.ExecuteAsync(null);

        Assert.True(underWay);
        Assert.True(_explore.Work.IsIdle);
        Assert.Equal(0, failures);
    });

    /// <summary>The removal took the file beside what was picked, so §5.6 did not pass.</summary>
    [Fact]
    public void ARemovalWhoseCheckFailsSaysItFailed() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(Path.GetPathRoot(_explore.Temp.Path)!);
        _explore.Bin = new FakeRecycleBin(path =>
        {
            Directory.Delete(path, recursive: true);
            File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "keep.bin"));

            return new RecycleOutcome(Removed: true);
        });
        var page = _explore.Page();
        var (tree, folder) = _explore.OnDisk();
        page.ScopeTo(tree.PathOf(tree.RootNode));
        await _explore.ScanAsync(page, ExploreScan.Fast(tree));
        var failures = 0;
        _explore.Work.Failed += (_, _) => failures++;

        page.Selection.Select([folder]);
        await page.Selection.DeleteCommand.ExecuteAsync(null);

        Assert.True(page.HasStatusChecks);
        Assert.True(_explore.Work.IsIdle);
        Assert.Equal(1, failures);
    });
}
