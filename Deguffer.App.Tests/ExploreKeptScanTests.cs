using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.History;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page wires the last kept scan of a drive while a new scan of it runs (#491): drawn
/// between the start of the scan and its first tree, for a scan of a whole volume with a kept scan,
/// and never acted on. When it is shown, what it draws and what it says are Core's, and tested there.
/// </summary>
public sealed class ExploreKeptScanTests : IDisposable
{
    private const string Volume = @"\\?\Volume{0b6f5c1e-3a7d-4c2b-9e10-6d2f1a4b8c33}\";

    private const long Megabyte = 1024L * 1024;

    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// A scan of a drive scanned before draws the kept scan as soon as it is found, with the line that
    /// says what it is and no empty state, and the finished tree replaces it.
    /// </summary>
    [Fact]
    public void TheKeptScanIsDrawnUntilTheFinishedTreeReplacesIt() => UiThread.Run(async () =>
    {
        await KeptAsync();
        _explore.Time.Advance(TimeSpan.FromDays(3));

        var page = _explore.Page();
        var redraws = 0;
        page.ViewChanged += (_, _) => redraws++;

        var running = page.ScanCommand.ExecuteAsync(null);

        Assert.Null(page.KeptScan.Tree);
        Assert.True(page.HasNoTree);

        // The start of the scan redraws for its own reasons. The kept scan arriving redraws once more.
        redraws = 0;
        await page.KeptScan.Lookup;

        Assert.NotNull(page.KeptScan.Tree);
        Assert.True(page.ShowsKept);
        Assert.False(page.HasNoTree);
        Assert.Null(page.Tree);
        Assert.Equal(1, redraws);
        Assert.Equal("Last scan, 3 days ago. Scanning again.", page.KeptScan.Mark);
        Assert.Equal(@"C:\", page.KeptScan.Tree.RootPath);
        Assert.Equal(
            ["Old", "Logs"],
            page.KeptScan.Tree.ChildrenOf(page.KeptScan.Tree.RootNode).ToArray().Select(node => page.KeptScan.Tree.NameOf(node)));

        var finished = Drive(("Logs", 250));
        _explore.Scanner.Current.Finish(ExploreScan.Fast(finished));
        await running;

        Assert.Null(page.KeptScan.Tree);
        Assert.False(page.ShowsKept);
        Assert.Same(finished, page.Tree);
        Assert.Equal(string.Empty, page.KeptScan.Mark);
    });

    /// <summary>The first snapshot of a walk replaces the kept scan, which does not come back for the rest of the scan.</summary>
    [Fact]
    public void TheFirstSnapshotReplacesTheKeptScan() => UiThread.Run(async () =>
    {
        await KeptAsync();
        var page = _explore.Page();

        var running = page.ScanCommand.ExecuteAsync(null);
        await page.KeptScan.Lookup;
        Assert.True(page.ShowsKept);

        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.Folder("Logs")]);
        var snapshot = builder.Snapshot(ExploreChildOrder.ByName);

        _explore.Scanner.Current.Report(new ExploreProgress(1, null, 0, snapshot));
        await Task.Yield();

        Assert.Null(page.KeptScan.Tree);
        Assert.False(page.ShowsKept);
        Assert.Same(snapshot, page.Tree);

        _explore.Scanner.Current.Finish(ExploreScan.Fast(builder.Build(ExploreChildOrder.BySize)));
        await running;

        Assert.Null(page.KeptScan.Tree);
    });

    /// <summary>A cancelled scan takes the kept scan with it, and the page is back to its empty state.</summary>
    [Fact]
    public void ACancelledScanDropsTheKeptScan() => UiThread.Run(async () =>
    {
        await KeptAsync();
        var page = _explore.Page();

        var running = page.ScanCommand.ExecuteAsync(null);
        await page.KeptScan.Lookup;
        Assert.True(page.ShowsKept);

        page.ScanCancelCommand.Execute(null);
        await running;

        Assert.Null(page.KeptScan.Tree);
        Assert.False(page.ShowsKept);
        Assert.Null(page.Tree);
        Assert.True(page.HasNoTree);
    });

    /// <summary>A scan that fails takes the kept scan with it too: nothing of the drive as it was is left behind.</summary>
    [Fact]
    public void AFailedScanDropsTheKeptScan() => UiThread.Run(async () =>
    {
        await KeptAsync();
        var page = _explore.Page();

        var running = page.ScanCommand.ExecuteAsync(null);
        await page.KeptScan.Lookup;
        Assert.True(page.ShowsKept);

        _explore.Scanner.Current.Fail(new IOException("The volume went away."));
        await running;

        Assert.Null(page.KeptScan.Tree);
        Assert.True(page.HasNoTree);
    });

    /// <summary>A scan of a folder on a drive with a kept scan draws nothing of it.</summary>
    [Fact]
    public void AScanOfAFolderDrawsNoKeptScan() => UiThread.Run(async () =>
    {
        await KeptAsync();
        var page = _explore.Page();
        page.Targeting.ScopeTo(@"C:\Users\testuser");

        var running = page.ScanCommand.ExecuteAsync(null);
        await page.KeptScan.Lookup;

        Assert.Null(page.KeptScan.Tree);
        Assert.True(page.HasNoTree);

        page.ScanCancelCommand.Execute(null);
        await running;
    });

    /// <summary>A scan of a drive with nothing kept draws nothing.</summary>
    [Fact]
    public void AScanOfADriveWithNothingKeptDrawsNothing() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte, volumeName: Volume);
        var page = _explore.Page();

        var running = page.ScanCommand.ExecuteAsync(null);
        await page.KeptScan.Lookup;

        Assert.Null(page.KeptScan.Tree);
        Assert.True(page.HasNoTree);

        page.ScanCancelCommand.Execute(null);
        await running;
    });

    /// <summary>
    /// A finished scan of the whole drive on screen is newer than the kept one, so scanning it again
    /// leaves it where it is.
    /// </summary>
    [Fact]
    public void ARescanOfTheDriveOnScreenKeepsItsTree() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte, volumeName: Volume);
        var page = _explore.Page();
        var first = Drive(("Logs", 10));
        await _explore.ScanAsync(page, ExploreScan.Fast(first));

        var running = page.ScanCommand.ExecuteAsync(null);
        await page.KeptScan.Lookup;

        Assert.Null(page.KeptScan.Tree);
        Assert.Same(first, page.Tree);

        page.ScanCancelCommand.Execute(null);
        await running;
    });

    /// <summary>
    /// A tree of another drive on screen gives way to the kept scan of the drive being scanned, with
    /// its rows and its selection, so nothing on the page describes a place the map is not drawing.
    /// </summary>
    [Fact]
    public void ATreeOfAnotherDriveGivesWayToTheKeptScan() => UiThread.Run(async () =>
    {
        await KeptAsync();
        _explore.Volumes.With(@"D:\", totalBytes: 500 * Megabyte, freeBytes: 100 * Megabyte);
        var page = _explore.Page();
        page.Targeting.ScopeTo(@"D:\");

        var other = new ExploreTreeBuilder(@"D:\");
        var games = other.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.Folder("Games")]);
        await _explore.ScanAsync(page, ExploreScan.Fast(other.Build(ExploreChildOrder.BySize)));
        page.Selection.Select([games]);
        Assert.NotEmpty(page.Selection.Nodes);

        page.Targeting.ScopeTo(@"C:\");
        var running = page.ScanCommand.ExecuteAsync(null);
        await page.KeptScan.Lookup;

        Assert.True(page.ShowsKept);
        Assert.Null(page.Tree);
        Assert.Empty(page.Selection.Nodes);

        page.ScanCancelCommand.Execute(null);
        await running;
    });

    /// <summary>
    /// §7.1: nothing on the kept scan can be removed. The page answers nothing about its shapes, and a
    /// pick on it leaves nothing Delete would act on.
    /// </summary>
    [Fact]
    public void APickOnTheKeptScanCannotBeRemoved() => UiThread.Run(async () =>
    {
        await KeptAsync();
        var page = _explore.Page();

        var running = page.ScanCommand.ExecuteAsync(null);
        await page.KeptScan.Lookup;

        var kept = page.KeptScan.Tree!;
        var logs = kept.ChildrenOf(kept.RootNode)[1];
        Assert.Equal("Logs", kept.NameOf(logs));

        Assert.Null(page.ShapeRemoval(kept, logs));
        Assert.Null(page.ShapeFacts(kept, logs));

        ((IMapNavigator)page).Pick(logs);

        Assert.False(page.Selection.CanAct);
        Assert.False(page.Selection.DeleteCommand.CanExecute(null));
        Assert.False(page.Selection.DeletePermanentlyCommand.CanExecute(null));

        page.ScanCancelCommand.Execute(null);
        await running;
    });

    /// <summary>
    /// The lists do not draw a kept scan, so a list view waits for the scan with its empty state, and
    /// the kept scan is drawn once the reader moves to a map.
    /// </summary>
    [Fact]
    public void AListViewDoesNotDrawTheKeptScan() => UiThread.Run(async () =>
    {
        await KeptAsync();
        var page = _explore.Listed();

        var running = page.ScanCommand.ExecuteAsync(null);
        await page.KeptScan.Lookup;

        Assert.NotNull(page.KeptScan.Tree);
        Assert.False(page.ShowsKept);
        Assert.True(page.HasNoTree);

        page.SelectedView = ExploreView.Treemap;

        Assert.True(page.ShowsKept);
        Assert.False(page.HasNoTree);

        page.ScanCancelCommand.Execute(null);
        await running;
    });

    /// <summary>Scan the whole of <c>C:\</c> once on a page of its own, so it has a kept scan.</summary>
    private async Task KeptAsync()
    {
        _explore.Volumes.With(@"C:\", totalBytes: 1000 * Megabyte, freeBytes: 400 * Megabyte, volumeName: Volume);
        await _explore.ScanAsync(_explore.Page(), ExploreScan.Fast(Drive(("Logs", 10), ("Old", 30))));

        Assert.Single(_explore.History.Kept(Volume));
    }

    /// <summary>A drive of top-level folders, each holding one file of the given number of megabytes.</summary>
    private static ExploreTree Drive(params (string Name, long Megabytes)[] folders)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var first = builder.AddChildren(ExploreTreeBuilder.RootNode, [.. folders.Select(folder => ExploreFixture.Folder(folder.Name))]);

        for (var i = 0; i < folders.Length; i++)
        {
            builder.AddChildren(first + i, [ExploreFixture.File("data.bin", folders[i].Megabytes * Megabyte)]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
