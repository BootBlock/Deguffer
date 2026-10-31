using Deguffer.App.Controls;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Windows.Foundation;

namespace Deguffer.App.Tests;

/// <summary>
/// How the map's pointer wires what <see cref="MapHitTest"/> finds to the readout, the outline and the
/// pointer's light: each is told at the rate it needs, and the readout never names a shape the pointer is
/// not over (§7.1). Where a point falls is tested in Core.
/// </summary>
public sealed class ExplorePointerTests
{
    private const int Width = 300;
    private const int Height = 100;

    /// <summary>Four files under the root, of which the drawing draws only the first three, side by side.</summary>
    private static readonly ExploreTree Tree = Files(4);

    private static readonly TiledSurface Drawing = new(
        Tree,
        Tree.RootNode,
        Width,
        Height,
        LayoutLimits.Default,
        ShapeColours.ByBranch(ExploreScheme.Standard),
        MapStyle.Default,
        [
            new ExploreTile(Tree.RootNode, Depth: 0, Bytes: 4000, X: 0, Y: 0, Width: Width, Height: Height),
            new ExploreTile(1, Depth: 1, Bytes: 1000, X: 0, Y: 0, Width: 100, Height: Height),
            new ExploreTile(2, Depth: 1, Bytes: 1000, X: 100, Y: 0, Width: 100, Height: Height),
            new ExploreTile(3, Depth: 1, Bytes: 1000, X: 200, Y: 0, Width: 100, Height: Height),
        ],
        MapViewport.Whole);

    private static readonly MapReading Reading = MapReading.Of(Tree, Tree)!;

    private static readonly Point FirstLeft = new(10, 50);
    private static readonly Point FirstRight = new(90, 50);
    private static readonly Point Second = new(150, 50);
    private static readonly Point Outside = new(500, 50);

    /// <summary>A pointer moving across one shape reports it once, and outlines it once, though the light follows every move.</summary>
    [Fact]
    public void APointerMovingAcrossOneShapeReportsItOnce()
    {
        var map = new Map();

        map.Pointer.MoveTo(FirstLeft);
        map.Pointer.Follow();
        map.Pointer.MoveTo(FirstRight);
        map.Pointer.Follow();

        Assert.Equal(new int?[] { 1 }, map.Reports.Select(hit => hit?.Node));
        Assert.Equal(1, map.Outlines);
        Assert.Equal(new MapLocation?[] { At(FirstLeft), At(FirstRight) }, map.Lights);
        Assert.Equal(At(FirstRight), map.Pointer.Over);
    }

    /// <summary>A pointer moving onto another shape reports and outlines that one, and moving off the picture reports nothing.</summary>
    [Fact]
    public void APointerMovingOntoAnotherShapeReportsIt()
    {
        var map = new Map();

        map.Pointer.MoveTo(FirstLeft);
        map.Pointer.Follow();
        map.Pointer.MoveTo(Second);
        map.Pointer.Follow();
        map.Pointer.MoveTo(Outside);
        map.Pointer.Follow();

        Assert.Equal(new int?[] { 1, 2, null }, map.Reports.Select(hit => hit?.Node));
        Assert.Equal(3, map.Outlines);
        Assert.Null(map.Pointer.Over);
    }

    /// <summary>
    /// The same shape found on a tile while the camera moves is outlined again from the tile, which the
    /// drawing worked from may not draw, and is not reported again.
    /// </summary>
    [Fact]
    public void TheSameShapeOnATileIsOutlinedAgainButNotReported()
    {
        var map = new Map();
        var onTile = new Point(1, 1);

        map.Pointer.MoveTo(FirstLeft);
        map.Pointer.Follow();

        var tile = Drawing.Tile(new MapTiling(Width, Height), new MapTileKey(0, 0, 0));
        var (x, y) = Enumerable.Range(0, tile.Width)
            .SelectMany(across => Enumerable.Range(0, tile.Height).Select(down => (X: (float)across, Y: (float)down)))
            .First(point => tile.At(point.X, point.Y)?.Node == At(FirstLeft).Hit!.Value.Node);

        map.Places[onTile] = new MapLocation(Drawing, MapViewport.Whole, x, y, tile, Reading);
        map.Pointer.MoveTo(onTile);
        map.Pointer.Follow();

        Assert.Equal(new int?[] { 1 }, map.Reports.Select(hit => hit?.Node));
        Assert.Equal(2, map.Outlines);
        Assert.Same(tile, map.Pointer.Over?.Tile);
    }

    /// <summary>
    /// A picture changed under a pointer that has not moved outlines what it is over every time, because
    /// the outline is somewhere else in new geometry, and reports only a change.
    /// </summary>
    [Fact]
    public void ARereadOutlinesEveryTimeAndReportsOnlyAChange()
    {
        var map = new Map();

        map.Pointer.MoveTo(FirstLeft);
        map.Pointer.Reread();
        map.Pointer.Reread();

        Assert.Equal(new int?[] { 1 }, map.Reports.Select(hit => hit?.Node));
        Assert.Equal(2, map.Outlines);
        Assert.Equal(2, map.Lights.Count);

        map.Places[FirstLeft] = At(Second);
        map.Pointer.Reread();

        Assert.Equal(new int?[] { 1, 2 }, map.Reports.Select(hit => hit?.Node));
    }

    /// <summary>A pointer that has left is over nothing: its light goes, the outline goes, and the readout says so.</summary>
    [Fact]
    public void APointerLeavingIsOverNothing()
    {
        var map = new Map();

        map.Pointer.MoveTo(FirstLeft);
        map.Pointer.Follow();
        map.Pointer.Leave();

        Assert.Null(map.Pointer.Position);
        Assert.Null(map.Pointer.Over);
        Assert.Null(map.Lights[^1]);
        Assert.Equal(2, map.Outlines);
        Assert.Equal(new int?[] { 1, null }, map.Reports.Select(hit => hit?.Node));

        // Nothing to follow until the pointer is back.
        map.Pointer.Follow();

        Assert.Equal(2, map.Reports.Count);
    }

    /// <summary>
    /// A picture gone with nothing in its place forgets what the pointer was over, so the same shape in the
    /// picture that replaces it is reported again: the map said nothing was under the pointer meanwhile.
    /// </summary>
    [Fact]
    public void AShapeIsReportedAgainOnceThePictureItWasInHasGone()
    {
        var map = new Map();

        map.Pointer.MoveTo(FirstLeft);
        map.Pointer.Follow();
        map.Pointer.Forget();
        map.Pointer.Reread();

        Assert.Equal(new int?[] { 1, 1 }, map.Reports.Select(hit => hit?.Node));
    }

    private static MapLocation At(Point point) =>
        new(Drawing, MapViewport.Whole, (float)point.X, (float)point.Y, Tile: null, Reading);

    private static ExploreTree Files(int count)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, count).Select(i => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>The map's side of the pointer: where each point falls, and what the pointer told it.</summary>
    private sealed class Map
    {
        public Map()
        {
            Places = new Dictionary<Point, MapLocation?>
            {
                [FirstLeft] = At(FirstLeft),
                [FirstRight] = At(FirstRight),
                [Second] = At(Second),
                [Outside] = null,
            };

            Pointer = new ExplorePointer(point => Places[point], Lights.Add, () => Outlines++, Reports.Add);
        }

        public Dictionary<Point, MapLocation?> Places { get; }

        public ExplorePointer Pointer { get; }

        public List<MapLocation?> Lights { get; } = [];

        public List<ExploreHit?> Reports { get; } = [];

        public int Outlines { get; private set; }
    }
}
