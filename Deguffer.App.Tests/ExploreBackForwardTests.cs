using Deguffer.App.ViewModels;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page wires Back and Forward: which of its steps are remembered, what a step back
/// redraws, and when Core's history is carried or forgotten. The rules themselves are
/// <see cref="ExploreVisits"/>'s, proved in Core.
/// </summary>
public sealed class ExploreBackForwardTests : IDisposable
{
    private static readonly MapViewport Zoomed = MapViewport.Fitting(new MapFrame(0.25, 0.25, 0.5, 0.5), MapCeiling.Least);

    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// Every way of moving is a step Back retraces, whatever route it took: opening a folder, going up,
    /// and a step on the trail. Forward goes again where Back came from.
    /// </summary>
    [Fact]
    public void BackAndForwardRetraceEveryKindOfStep() => UiThread.Run(async () =>
    {
        var (page, tree, users, profile) = await ScannedAsync();
        var top = page.Trail[0].Position;

        Assert.False(page.GoBackCommand.CanExecute(null));

        page.Descend(users);
        page.Descend(profile);
        page.AscendCommand.Execute(null);
        page.GoTo(top);

        Assert.Equal(tree.RootNode, page.CurrentNode);

        page.GoBackCommand.Execute(null);
        Assert.Equal(users, page.CurrentNode);
        Assert.Equal(["testuser"], page.Rows.Select(row => row.Name));

        page.GoBackCommand.Execute(null);
        Assert.Equal(profile, page.CurrentNode);

        page.GoBackCommand.Execute(null);
        page.GoBackCommand.Execute(null);
        Assert.Equal(tree.RootNode, page.CurrentNode);
        Assert.False(page.GoBackCommand.CanExecute(null));

        page.GoForwardCommand.Execute(null);
        Assert.Equal(users, page.CurrentNode);
        Assert.True(page.GoForwardCommand.CanExecute(null));
    });

    /// <summary>
    /// A step on the trail to where the reader already is goes nowhere, so it is not a step, and
    /// does not forget what lay ahead as a step would.
    /// </summary>
    [Fact]
    public void TheStepTheReaderIsOnIsNotAStep() => UiThread.Run(async () =>
    {
        var (page, _, users, _) = await ScannedAsync();

        page.Descend(users);
        page.GoBackCommand.Execute(null);
        page.GoTo(page.Trail[^1].Position);

        Assert.True(page.GoForwardCommand.CanExecute(null));
    });

    /// <summary>
    /// Back returns to the zoom the reader left a place at, for the one redraw it asks for, and a zoom
    /// to a shape the map could not open is a step of its own, in the same place.
    /// </summary>
    [Fact]
    public void BackReturnsToTheZoomItLeft() => UiThread.Run(async () =>
    {
        var (page, tree, users, _) = await ScannedAsync();
        var viewing = MapViewport.Whole;
        var redrawnAt = new List<MapViewport?>();

        page.Viewing = () => viewing;
        page.ViewChanged += (_, _) => redrawnAt.Add(page.Revisiting);

        var told = 0;
        page.GoBackCommand.CanExecuteChanged += (_, _) => told++;

        page.Zoomed(viewing);

        Assert.Equal(1, told);
        viewing = Zoomed;
        page.Descend(users);
        viewing = MapViewport.Whole;

        page.GoBackCommand.Execute(null);

        Assert.Equal(tree.RootNode, page.CurrentNode);
        Assert.Equal(Zoomed, redrawnAt[^1]);
        Assert.Null(page.Revisiting);

        viewing = Zoomed;
        page.GoBackCommand.Execute(null);

        Assert.Equal(tree.RootNode, page.CurrentNode);
        Assert.Equal(MapViewport.Whole, redrawnAt[^1]);

        // Any other redraw keeps the zoom the reader has now.
        var redraws = redrawnAt.Count;
        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(@"C:\")));

        Assert.True(redrawnAt.Count > redraws);
        Assert.All(redrawnAt.Skip(redraws), viewport => Assert.Null(viewport));
    });

    /// <summary>
    /// Forward is offered once Back has undone a zoom, which is asked only after the redraw has moved
    /// the map's zoom: asked before, the map still answers with the zoom being left, and the step
    /// ahead reads as a step to where the reader already is. And a zoom that comes to rest anywhere
    /// asks again.
    /// </summary>
    [Fact]
    public void ForwardIsOfferedOnceTheZoomHasMoved() => UiThread.Run(async () =>
    {
        var (page, _, _, _) = await ScannedAsync();
        var viewing = MapViewport.Whole;
        var offered = new List<bool>();

        // The map: a redraw that hands it a zoom puts it there before the redraw returns.
        page.Viewing = () => viewing;
        page.ViewChanged += (_, _) => viewing = page.Revisiting ?? viewing;
        page.GoForwardCommand.CanExecuteChanged += (_, _) => offered.Add(page.GoForwardCommand.CanExecute(null));

        page.Zoomed(viewing);
        viewing = Zoomed;

        page.GoBackCommand.Execute(null);

        Assert.Equal(MapViewport.Whole, viewing);
        Assert.True(offered[^1]);

        var asked = offered.Count;
        page.ViewportChanged();

        Assert.Equal(asked + 1, offered.Count);
    });

    /// <summary>
    /// A view that shows no zoom offers no step that only zooms, and is asked again as the view
    /// changes.
    /// </summary>
    [Fact]
    public void AViewThatShowsNoZoomOffersNoStepThatOnlyZooms() => UiThread.Run(async () =>
    {
        var (page, _, _, _) = await ScannedAsync();
        var viewing = MapViewport.Whole;
        var told = 0;

        page.Viewing = () => viewing;
        page.GoBackCommand.CanExecuteChanged += (_, _) => told++;

        // A zoom to a shape, then a turn of the wheel back out to the whole picture: Back on the
        // treemap zooms in again.
        viewing = Zoomed;
        page.Zoomed(viewing);
        viewing = MapViewport.Whole;

        Assert.True(page.GoBackCommand.CanExecute(null));

        told = 0;
        page.SelectedView = Core.Configuration.ExploreView.Sunburst;

        Assert.True(told > 0);
        Assert.False(page.GoBackCommand.CanExecute(null));
    });


    /// <summary>
    /// A rescan of the same place keeps where the reader has been, and a scan of somewhere else, or a
    /// cancelled one, forgets it.
    /// </summary>
    [Fact]
    public void ARescanKeepsTheStepsAndAnotherPlaceForgetsThem() => UiThread.Run(async () =>
    {
        var (page, _, users, profile) = await ScannedAsync();

        page.Descend(users);
        page.Descend(profile);
        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(@"C:\")));

        Assert.Equal(profile, page.CurrentNode);
        Assert.True(page.GoBackCommand.CanExecute(null));

        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(@"D:\")));

        Assert.False(page.GoBackCommand.CanExecute(null));

        page.Descend(users);
        page.Descend(profile);

        var told = 0;
        page.GoBackCommand.CanExecuteChanged += (_, _) => told++;

        var running = page.ScanCommand.ExecuteAsync(null);
        page.ScanCancelCommand.Execute(null);
        await running;

        Assert.False(page.GoBackCommand.CanExecute(null));
        Assert.True(told > 0);

        // The next tree is the first one, as far as where the reader has been goes.
        await _explore.ScanAsync(page, ExploreScan.Fast(Drive(@"C:\")));

        Assert.False(page.GoBackCommand.CanExecute(null));
    });

    /// <summary>
    /// A folder removed from the page is passed over, as it cannot be opened by any other route, and
    /// the button stops offering it once the removal is done.
    /// </summary>
    [Fact]
    public void ARemovedFolderIsNotSteppedBackInto() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(Path.GetPathRoot(_explore.Temp.Path)!);
        var page = _explore.Page();
        var (tree, folder) = _explore.OnDisk();

        page.ScopeTo(tree.PathOf(tree.RootNode));
        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        page.Descend(folder);
        page.GoBackCommand.Execute(null);

        Assert.True(page.GoForwardCommand.CanExecute(null));

        var told = 0;
        page.GoForwardCommand.CanExecuteChanged += (_, _) => told++;

        page.Selection.Select([folder]);
        await page.Selection.DeleteCommand.ExecuteAsync(null);

        Assert.False(page.GoForwardCommand.CanExecute(null));
        Assert.True(told > 0);

        page.GoForwardCommand.Execute(null);

        Assert.Equal(tree.RootNode, page.CurrentNode);
    });

    private async Task<(ExploreViewModel Page, ExploreTree Tree, int Users, int Profile)> ScannedAsync()
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        var tree = Drive(@"C:\");

        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        var users = tree.ChildrenOf(tree.RootNode)[0];

        return (page, tree, users, tree.ChildrenOf(users)[0]);
    }

    /// <summary>Users holding a profile holding a file, beside a file.</summary>
    private static ExploreTree Drive(string root)
    {
        var builder = new ExploreTreeBuilder(root);
        var users = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Folder("Users"),
            ExploreFixture.File("pagefile.sys", 100),
        ]);
        var profile = builder.AddChildren(users, [ExploreFixture.Folder("testuser")]);

        builder.AddChildren(profile, [ExploreFixture.File("ntuser.dat", 1_000)]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
