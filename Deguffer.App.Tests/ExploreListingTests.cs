using System.Collections.Specialized;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using static Deguffer.App.Tests.CollectionChanges;

namespace Deguffer.App.Tests;

/// <summary>
/// The folder's rows are written only while the List layout is on screen, because nothing else reads
/// them and a folder can hold tens of thousands of entries. The trail is above the map too, so it
/// moves whatever is on screen.
/// </summary>
public sealed class ExploreListingTests : IDisposable
{
    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// Under the treemap, the snapshots of a running scan, the finished tree and a step into a folder
    /// leave the rows alone, and the trail still names where the reader is.
    /// </summary>
    [Fact]
    public void UnderAMapTheRowsAreLeftAloneAndTheTrailStillMoves() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var changes = Watch(page.Listing.Rows);
        var builder = Drive(out var users);

        var running = page.ScanCommand.ExecuteAsync(null);
        var scan = _explore.Scanner.Current;

        scan.Report(new ExploreProgress(2, null, 0, builder.Snapshot(ExploreChildOrder.BySize)));
        await Task.Yield();

        builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.File("hiberfil.sys", 50)]);
        scan.Report(new ExploreProgress(3, null, 0, builder.Snapshot(ExploreChildOrder.BySize)));
        await Task.Yield();

        scan.Finish(ExploreScan.Fast(builder.Build(ExploreChildOrder.BySize)));
        await running;

        page.Descend(users);

        Assert.Equal(users, page.CurrentNode);
        Assert.Equal("Users", page.Listing.Trail[^1].Name);
        Assert.Empty(page.Listing.Rows);
        Assert.Empty(changes);
    });

    /// <summary>
    /// Choosing the List layout lists the folder on screen, not the one the reader was in when the map
    /// was chosen, and the page is told the list is being rewritten for every change it makes.
    /// </summary>
    [Fact]
    public void ChoosingTheListListsTheFolderOnScreen() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var during = new List<bool>();

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(out var users).Build(ExploreChildOrder.BySize)));
        page.Descend(users);

        page.Listing.Rows.CollectionChanged += (_, _) => during.Add(page.IsShowingRows);
        page.SelectedView = ExploreView.List;

        Assert.Equal(["testuser", "Public"], page.Listing.Rows.Select(row => row.Name));
        Assert.NotEmpty(during);
        Assert.All(during, Assert.True);
        Assert.False(page.IsShowingRows);
    });

    /// <summary>
    /// A reader who goes from the list to the map and back without moving comes back to the same rows,
    /// so the list keeps their place in it, and is told nothing.
    /// </summary>
    [Fact]
    public void BackFromTheMapInTheSameFolderTheRowsAreTheSameRows() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Listed();

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(out _).Build(ExploreChildOrder.BySize)));

        ExploreRow[] before = [.. page.Listing.Rows];
        var changes = Watch(page.Listing.Rows);

        page.SelectedView = ExploreView.Treemap;
        page.SelectedView = ExploreView.List;

        Assert.Equal(before, page.Listing.Rows);
        Assert.Empty(changes);
    });

    /// <summary>
    /// A snapshot of the same folder in the same order that lands while the icicle is on screen is
    /// brought into the same rows once the list comes back: what arrived is added, and what stayed is
    /// the same row.
    /// </summary>
    [Fact]
    public void ASnapshotUnderTheMapIsBroughtIntoTheSameRowsWhenTheListComesBack() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Listed();
        var builder = Drive(out _);

        var running = page.ScanCommand.ExecuteAsync(null);
        var scan = _explore.Scanner.Current;

        scan.Report(new ExploreProgress(2, null, 0, builder.Snapshot(ExploreChildOrder.ByName)));
        await Task.Yield();

        var pagefile = page.Listing.Rows.Single(row => row.Name == "pagefile.sys");
        var changes = Watch(page.Listing.Rows);

        page.SelectedView = ExploreView.Icicle;

        builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.File("hiberfil.sys", 50)]);
        scan.Report(new ExploreProgress(3, null, 0, builder.Snapshot(ExploreChildOrder.ByName)));
        await Task.Yield();

        Assert.Empty(changes);

        page.SelectedView = ExploreView.List;

        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
        Assert.Same(pagefile, page.Listing.Rows.Single(row => row.Name == "pagefile.sys"));
        Assert.Contains(page.Listing.Rows, row => row.Name == "hiberfil.sys");

        scan.Finish(ExploreScan.Fast(builder.Build(ExploreChildOrder.BySize)));
        await running;
    });

    /// <summary>
    /// A step taken under the map makes the rows held a list about another folder, so the list that
    /// comes back is rebuilt for the folder on screen in one reset.
    /// </summary>
    [Fact]
    public void AStepUnderTheMapRebuildsTheListWhenItComesBack() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Listed();

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(out var users).Build(ExploreChildOrder.BySize)));

        var changes = Watch(page.Listing.Rows);

        page.SelectedView = ExploreView.Treemap;
        page.Descend(users);
        page.SelectedView = ExploreView.List;

        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        Assert.Equal(["testuser", "Public"], page.Listing.Rows.Select(row => row.Name));
    });

    /// <summary>
    /// Out and back under the map ends in the folder the rows were about, but each step starts the
    /// list afresh, as it does with the list on screen, so the rows that come back are new rows rather
    /// than the old ones reconciled.
    /// </summary>
    [Fact]
    public void AStepInAndOutUnderTheMapStillRebuildsTheList() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Listed();

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(out var users).Build(ExploreChildOrder.BySize)));

        var before = page.Listing.Rows.Single(row => row.Name == "Users");
        var changes = Watch(page.Listing.Rows);

        page.SelectedView = ExploreView.Treemap;
        page.Descend(users);
        page.AscendCommand.Execute(null);
        page.SelectedView = ExploreView.List;

        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        Assert.Equal(["Users", "pagefile.sys"], page.Listing.Rows.Select(row => row.Name));
        Assert.NotSame(before, page.Listing.Rows.Single(row => row.Name == "Users"));
    });

    /// <summary>A cancelled scan leaves nothing to list when the list is chosen afterwards.</summary>
    [Fact]
    public void AfterACancelledScanTheListHasNothingToList() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();

        var running = page.ScanCommand.ExecuteAsync(null);
        var scan = _explore.Scanner.Current;

        scan.Report(new ExploreProgress(2, null, 0, Drive(out _).Snapshot(ExploreChildOrder.BySize)));
        await Task.Yield();

        page.ScanCancelCommand.Execute(null);
        await running;

        page.SelectedView = ExploreView.List;

        Assert.Null(page.Tree);
        Assert.Empty(page.Listing.Rows);
        Assert.Empty(page.Listing.Trail);
    });

    /// <summary>A drive holding a folder of two and a file, the folder first by size.</summary>
    private static ExploreTreeBuilder Drive(out int users)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        users = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Folder("Users"),
            ExploreFixture.File("pagefile.sys", 100),
        ]);

        builder.AddChildren(users, [
            ExploreFixture.File("testuser", 300),
            ExploreFixture.File("Public", 200),
        ]);

        return builder;
    }
}
