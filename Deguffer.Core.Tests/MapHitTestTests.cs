using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// While a redraw lands, each point of the map is answered by the drawing the screen shows there:
/// the redraw's, where its region has landed, and the last drawing's everywhere else (§7.1).
///
/// <para>A right-click picks what the menu acts on, so an answer from a drawing that is not on screen
/// at that point is a Delete aimed at a shape the user never pointed at. These run a real redraw on
/// a queued owner's thread and hold it part of the way through landing, so the state tested is the
/// one the map is in between two hand-overs.</para>
/// </summary>
public sealed class MapHitTestTests
{
    private const int Old = 1;
    private const int New = 2;

    private readonly QueuedContext _owner = new();
    private readonly CanvasRedraws _redraws;

    public MapHitTestTests() => _redraws = new CanvasRedraws(_owner);

    /// <summary>
    /// The top left region has landed and the rest have not: a point there is the new drawing's, and
    /// a point anywhere else is the old one's, because the old picture is what shows there.
    /// </summary>
    [Fact]
    public void APointResolvesAgainstThePictureOnScreenThere()
    {
        var tree = Drawings.Tree();
        var old = Drawings.Covering(tree, Old);
        var (arriving, gated) = LandTopLeft(tree);

        Assert.Equal(New, NodeAt(0.1, 0.1, old, arriving, tree));
        Assert.Equal(Old, NodeAt(0.9, 0.1, old, arriving, tree));
        Assert.Equal(Old, NodeAt(0.1, 0.9, old, arriving, tree));
        Assert.Equal(Old, NodeAt(0.9, 0.9, old, arriving, tree));

        gated.Open();
    }

    /// <summary>
    /// A redraw superseded part of the way through landing never answers again, even at the points
    /// its regions covered: they came off the screen with it.
    /// </summary>
    [Fact]
    public async Task ASupersededRedrawNeverAnswers()
    {
        var tree = Drawings.Tree();
        var old = Drawings.Covering(tree, Old);
        var (arriving, gated) = LandTopLeft(tree);

        _redraws.Cancel();

        foreach (var (x, y) in Grid())
        {
            Assert.Equal(Old, NodeAt(x, y, old, arriving, tree));
        }

        gated.Open();
        await arriving.Finished.WaitAsync(TimeSpan.FromSeconds(20));
        _owner.RunPending();

        foreach (var (x, y) in Grid())
        {
            Assert.Equal(Old, NodeAt(x, y, old, arriving, tree));
        }
    }

    /// <summary>
    /// A redraw of a new tree landing over a drawing of the old one: the landed region answers in the
    /// new tree, and the old drawing showing round it answers in the new tree too, with the node that
    /// is the same thing there. The newer tree numbered it otherwise, so a number read as it is would
    /// name another file.
    /// </summary>
    [Fact]
    public void ADrawingOfAnOlderTreeOfTheSamePlaceAnswersInTheNewer()
    {
        var older = Drawings.Tree(["file0", "file1", "file2"]);
        var newer = Drawings.Tree(["early", "file0", "file1", "file2"]);
        var old = Drawings.Covering(older, Old);
        var (arriving, gated) = LandTopLeft(newer);

        Assert.Equal("file0", older.NameOf(Old));
        Assert.Equal("file0", newer.NameOf(New));
        Assert.Equal(New, NodeAt(0.9, 0.9, old, arriving, newer));
        Assert.Equal(New, NodeAt(0.5, 0.5, old, arriving: null, newer));

        gated.Open();
    }

    /// <summary>
    /// What the older drawing shows and the newer tree does not hold answers nothing: a file removed
    /// since, and the whole of a drawing of another place (§7.1).
    /// </summary>
    [Fact]
    public void AShapeWithNoCounterpartInTheNewerTreeAnswersNothing()
    {
        var older = Drawings.Tree(files: 3);
        var old = Drawings.Covering(older, Old);

        Assert.Null(MapHitTest.Locate(MapViewport.Whole, 0.5, 0.5, old, arriving: null, ReadingIn(older.Without([Old])))?.Hit);
        Assert.Null(MapHitTest.Locate(MapViewport.Whole, 0.5, 0.5, old, arriving: null, ReadingIn(Drawings.Tree(files: 3, root: @"D:\"))));
        Assert.Equal(Old, NodeAt(0.5, 0.5, old, arriving: null, older.Without([Old + 1])));
    }

    /// <summary>
    /// A point is taken back through where the drawing is placed on a zoomed screen, and a point past
    /// the edge of a drawing of part of the picture is over nothing.
    /// </summary>
    [Fact]
    public void APointIsFoundThroughTheZoomOnScreen()
    {
        var tree = Drawings.Tree();
        var zoomedIn = MapViewport.Anchored(2, 0, 0, 0, 0, MapCeiling.Least);
        var drawing = Drawings.Covering(tree, Old, zoomedIn);

        var middle = MapHitTest.Locate(zoomedIn, 0.5, 0.5, drawing, arriving: null, ReadingIn(tree));

        Assert.NotNull(middle);
        Assert.Equal(Drawings.Size / 2f, middle.Value.X, 1);
        Assert.Equal(Drawings.Size / 2f, middle.Value.Y, 1);

        // Zoomed out to the whole picture, the drawing of its top left quarter covers only that.
        Assert.NotNull(MapHitTest.Locate(MapViewport.Whole, 0.25, 0.25, drawing, arriving: null, ReadingIn(tree)));
        Assert.Null(MapHitTest.Locate(MapViewport.Whole, 0.75, 0.75, drawing, arriving: null, ReadingIn(tree)));
    }

    /// <summary>
    /// While the camera moves, a tile drawn more finely than the drawing shows over it, and draws what
    /// the drawing does not: a point it covers is its own, and every other point is the drawing's.
    /// </summary>
    [Fact]
    public void ATileShownOverTheDrawingAnswersWhereItShows()
    {
        var tree = Drawings.Tree();
        var old = Drawings.Covering(tree, Old, MapViewport.Whole);
        var tile = CornerTile(old);

        Assert.Equal(New, NodeAt(0.95, 0.95, old, arriving: null, tree, tile));
        Assert.Equal(Old, NodeAt(0.05, 0.05, old, arriving: null, tree, tile));
        Assert.Equal(Old, NodeAt(0.95, 0.95, old, arriving: null, tree));

        var spot = MapHitTest.Locate(MapViewport.Whole, 0.95, 0.95, old, arriving: null, ReadingIn(tree), Shown(tile))!.Value;
        var shape = spot.ShapeOnScreen(MapViewport.Whole)!.Value;

        Assert.Same(tile, spot.Tile);
        Assert.True(shape.Contains(0.95, 0.95) && shape.X > 0.45 && shape.X + shape.Width <= 1 + 1e-9, $"the shape found is at {shape}");
    }

    /// <summary>
    /// A tile drawn less finely than the drawing is under it, so it answers nothing: the drawing is what
    /// shows over it.
    /// </summary>
    [Fact]
    public void ATileUnderTheDrawingDoesNotAnswer()
    {
        var tree = Drawings.Tree();
        var zoomedIn = MapViewport.Anchored(MapTiling.ZoomOf(4), 1, 1, 1, 1, MapCeiling.Least);
        var old = Drawings.Covering(tree, Old, zoomedIn);
        var tile = CornerTile(old, level: 3);
        var (x, y) = zoomedIn.PictureAt(0.5, 0.5);

        var spot = MapHitTest.Locate(zoomedIn, 0.5, 0.5, old, arriving: null, ReadingIn(tree), Shown(tile));

        Assert.True(tile.Frame.Contains(x, y));
        Assert.Null(spot!.Value.Tile);
        Assert.Equal(Old, spot.Value.Hit?.Node);
    }

    /// <summary>A redraw's landed region shows over every tile, so it answers there.</summary>
    [Fact]
    public void ALandedRegionAnswersOverATile()
    {
        const int Drawn = 3;

        var tree = Drawings.Tree(files: 3);
        var old = Drawings.Covering(tree, Drawn, MapViewport.Whole);
        var topLeft = old.Tile(new MapTiling(Drawings.Size, Drawings.Size), new MapTileKey(0, 0, 0));
        var (arriving, gated) = LandTopLeft(tree);

        Assert.Equal(Old, NodeAt(0.1, 0.1, old, arriving: null, tree, topLeft));
        Assert.Equal(New, NodeAt(0.1, 0.1, old, arriving, tree, topLeft));

        gated.Open();
    }

    /// <summary>
    /// A tile of an older tree is read as a drawing of one is: in the newer tree where it is of the same
    /// place, and not at all where it is of another.
    /// </summary>
    [Fact]
    public void ATileOfAnOlderTreeIsReadInTheNewer()
    {
        var older = Drawings.Tree(["file0", "file1"]);
        var newer = Drawings.Tree(["early", "file0", "file1"]);
        var drawing = Drawings.Covering(newer, Old, MapViewport.Whole);
        var tile = CornerTile(Drawings.Covering(older, Old, MapViewport.Whole));
        var elsewhere = CornerTile(Drawings.Covering(Drawings.Tree(root: @"D:\"), Old, MapViewport.Whole));

        Assert.Equal(newer.ChildrenOf(newer.RootNode).ToArray().Single(node => newer.NameOf(node) == "file1"), NodeAt(0.95, 0.95, drawing, arriving: null, newer, tile));
        Assert.Null(MapHitTest.Locate(MapViewport.Whole, 0.95, 0.95, drawing, arriving: null, ReadingIn(newer), Shown(elsewhere)));
    }

    /// <summary>
    /// Start a redraw of <paramref name="tree"/> that is <see cref="New"/> everywhere, and stop it
    /// with only its top left region on screen.
    /// </summary>
    private (CanvasRedraw Arriving, GatedSurface Gated) LandTopLeft(Exploring.ExploreTree tree)
    {
        var gated = new GatedSurface(Drawings.Covering(tree, New), region => region is { X: 0, Y: 0 });

        var arriving = _redraws.Start(
            () => gated,
            new byte[PixelBuffer.LengthFor(Drawings.Size, Drawings.Size)],
            Drawings.Ground,
            new ExplorePoint(1, 1),
            new RedrawRecorder());

        _owner.RunUntil(() => arriving.Landed.Count > 0, "the top left region to land");

        Assert.Equal([new CanvasRegion(0, 0, PaintOrder.RegionSize, PaintOrder.RegionSize)], arriving.Landed);

        return (arriving, gated);
    }

    private static int? NodeAt(
        double x, double y, ExploreSurface old, CanvasRedraw? arriving, ISizedTree tree, MapTile? over = null) =>
        MapHitTest.Locate(MapViewport.Whole, x, y, old, arriving, ReadingIn(tree), Shown(over)) is { } spot
            ? spot.Hit?.Node
            : null;

    /// <summary>How the map reads a drawing in <paramref name="tree"/>, the tree the page holds.</summary>
    private static Func<ISizedTree, MapReading?> ReadingIn(ISizedTree tree) => drawn => MapReading.Of(drawn, tree);

    /// <summary>What shows <paramref name="tile"/> at the points of the picture it covers, and no tile anywhere else.</summary>
    private static Func<double, double, MapTile?>? Shown(MapTile? tile) =>
        tile is null ? null : (x, y) => tile.Frame.Contains(x, y) ? tile : null;

    /// <summary>
    /// The tile of <paramref name="drawing"/>'s picture at its bottom right, cut from it at
    /// <paramref name="level"/>. Laid out for real, the picture is its first file down the left half
    /// and its second, node 2, down the right, which is all the bottom right tile shows.
    /// </summary>
    private static MapTile CornerTile(TiledSurface drawing, int level = 0)
    {
        var tiling = new MapTiling(Drawings.Size, Drawings.Size);

        return drawing.Tile(tiling, tiling.At(level, 0.95, 0.95)!.Value);
    }

    /// <summary>Points across the whole screen, a few in each region.</summary>
    private static IEnumerable<(double X, double Y)> Grid() =>
        from x in Enumerable.Range(0, 8)
        from y in Enumerable.Range(0, 8)
        select ((x + 0.5) / 8, (y + 0.5) / 8);
}
