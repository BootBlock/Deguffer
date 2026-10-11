using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The tiles a moving map is drawn from are pieces of the one picture: a shape is where every level puts
/// it, and a tile draws exactly what the drawing made at rest draws, so nothing moves, appears or goes
/// as a move gives way to the drawing at its end.
/// </summary>
public sealed class MapTileLayoutTests
{
    private const int Width = 800;
    private const int Height = 600;

    /// <summary>Within a hundredth of a pixel of the finer of the two pictures compared.</summary>
    private const double Tolerance = 0.01;

    private static readonly ExploreTree Tree = Folders();

    private static readonly MapTiling Tiling = new(Width, Height);

    /// <summary>
    /// Every shape a tile draws is drawn in the same place, magnified, by the tiles of the next level
    /// over the same part of the picture, which add only what the deeper zoom opens.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void AShapeIsWhereTheNextLevelPutsIt(int level)
    {
        var picture = Drawing(MapViewport.Anchored(MapTiling.ZoomOf(level), 0.37, 0.61, 0.5, 0.5, MapCeiling.Most));
        var coarse = picture.Tile(Tiling, Tiling.At(level, 0.37, 0.61)!.Value);
        var finer = Overlapping(picture, coarse.Frame, level + 1);

        var shapes = InPicture(coarse);
        var deeper = finer.SelectMany(InPicture).ToLookup(shape => shape.Node);
        var across = Tiling.AcrossAt(level + 1);

        Assert.NotEmpty(shapes);
        Assert.True(deeper.Count > shapes.Count, "the next level opened nothing, so it proves nothing");

        foreach (var shape in shapes)
        {
            Assert.Contains(
                deeper[shape.Node],
                found => Same(shape.Frame, found.Frame, across));
        }
    }

    /// <summary>
    /// The tiles a zoom is shown from draw what the drawing made at that zoom draws, in the same places:
    /// the same folders opened, the same files drawn one by one, and nothing more. Only finer, so the
    /// drawing that arrives at rest replaces them without a shape moving or a folder closing.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.3)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(3.3)]
    [InlineData(5.9)]
    [InlineData(11.0)]
    [InlineData(23.0)]
    public void ATileDrawsWhatTheDrawingAtThatZoomDraws(double zoom)
    {
        var viewport = MapViewport.Anchored(zoom, 0.42, 0.33, 0.5, 0.5, MapCeiling.Most);
        var drawing = Drawing(viewport);
        var level = MapTiling.LevelOf(zoom);
        var screen = viewport.PictureOf(MapFrame.Whole);
        var tiles = Tiling.Covering(viewport, level, 0.5, 0.5).Select(key => drawing.Tile(Tiling, key)).ToList();

        var drawn = drawing.Tiles
            .Where(shape => shape.IsNode)
            .Select(shape => (shape.Node, Frame: Fraction(shape, viewport)))
            .Where(shape => Overlaps(shape.Frame, screen))
            .ToDictionary(shape => shape.Node, shape => shape.Frame);
        var tiled = tiles
            .SelectMany(InPicture)
            .Where(shape => Overlaps(shape.Frame, screen))
            .ToLookup(shape => shape.Node);

        Assert.Equal(drawn.Keys.Order(), tiled.Select(group => group.Key).Order());

        foreach (var (node, frame) in drawn)
        {
            Assert.All(tiled[node], found => Assert.True(
                Same(frame, found.Frame, Tiling.AcrossAt(level)),
                $"node {node} is at {found.Frame} in a tile and at {frame} in the drawing"));
        }
    }

    /// <summary>The tiles of one level over a part of the picture tile it, with no gap and no overlap.</summary>
    [Fact]
    public void TheTilesOfALevelCoverWhatIsShownExactly()
    {
        var viewport = MapViewport.Anchored(6.5, 0.2, 0.8, 0.5, 0.5, MapCeiling.Most);
        var level = MapTiling.LevelOf(viewport.Zoom);
        var frames = Tiling.Covering(viewport, level, 0.2, 0.8).Select(Tiling.FrameOf).ToList();
        var screen = viewport.PictureOf(MapFrame.Whole);

        foreach (var (x, y) in Grid(screen).Append((screen.X, screen.Y)))
        {
            Assert.Equal(1, frames.Count(frame => x >= frame.X && x < frame.X + frame.Width && y >= frame.Y && y < frame.Y + frame.Height));
        }
    }

    /// <summary>The tile under the point a reader is looking at is the first wanted.</summary>
    [Fact]
    public void TheTileAtTheFocusIsWantedFirst()
    {
        var viewport = MapViewport.Anchored(4, 0.5, 0.5, 0.5, 0.5, MapCeiling.Most);
        var level = MapTiling.LevelOf(viewport.Zoom);

        var keys = Tiling.Covering(viewport, level, 0.41, 0.57);

        Assert.Equal(Tiling.At(level, 0.41, 0.57), keys[0]);
    }

    /// <summary>
    /// The tile holding the focus is wanted first even where a short tile at the picture's edge has its
    /// middle nearer the focus than the tile's own has.
    /// </summary>
    [Fact]
    public void TheTileAtTheFocusIsWantedFirstBesideAShortEdgeTile()
    {
        // At level 0 a 600 by 400 picture is 848.5 pixels across: three whole columns and one 80.5
        // pixels wide. A focus at 760 is 120 pixels from the middle of its own column, and 48 from the
        // middle of the short one beside it.
        var tiling = new MapTiling(600, 400);
        var focusX = 760 / tiling.AcrossAt(0);
        var focusY = 128 / tiling.DownAt(0);

        var keys = tiling.Covering(MapViewport.Whole, 0, focusX, focusY);

        Assert.Equal(new MapTileKey(0, 2, 0), keys[0]);
    }

    /// <summary>
    /// No screen at any zoom needs more tiles than <see cref="MapTiling.MostOnScreen"/>, so a budget of
    /// screens of them always holds the screen and where a move is going, however small the map.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 60)]
    [InlineData(600, 400)]
    [InlineData(1920, 1080)]
    public void TheBudgetHoldsEveryTileOfTheScreenAndOfWhereItIsGoing(int width, int height)
    {
        var tiling = new MapTiling(width, height);
        var most = 0;

        foreach (var zoom in Enumerable.Range(0, 1200).Select(i => Math.Pow(2, i / 100.0)))
        {
            foreach (var (x, y) in new[] { (0.0, 0.0), (0.3, 0.7), (0.5, 0.5), (0.999, 0.001), (0.123, 0.456) })
            {
                var shown = MapViewport.Anchored(zoom, x, y, 0.5, 0.5, MapCeiling.Most);
                var keys = tiling.Covering(shown, MapTiling.LevelOf(shown.Zoom), x, y);

                most = Math.Max(most, keys.Count);
            }
        }

        Assert.True(most <= tiling.MostOnScreen, $"a screen needed {most} tiles, more than the {tiling.MostOnScreen} allowed for");
        Assert.True(
            tiling.Budget >= 2 * tiling.MostOnScreen * PixelBuffer.LengthFor(MapTiling.Size, MapTiling.Size),
            $"a budget of {tiling.Budget} bytes cannot hold two screens of {tiling.MostOnScreen} tiles");
    }

    /// <summary>A tile is placed where it lies in the picture, as a drawing's canvas is placed.</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(6, 5, 4)]
    public void ATileIsPlacedWhereItLiesInThePicture(int level, long column, long row)
    {
        var tile = Drawing(MapViewport.Whole).Tile(Tiling, new MapTileKey(level, column, row));
        var origin = new MapOrigin(0.25, 0.125);
        const double ScreenWidth = 1000;
        const double ScreenHeight = 750;

        var placed = tile.Canvas(ScreenWidth, ScreenHeight, origin);
        var (left, top) = placed.Apply(0, 0);
        var (right, bottom) = placed.Apply(tile.Width, tile.Height);

        Assert.Equal((tile.Frame.X - origin.Left) * ScreenWidth, left, 6);
        Assert.Equal((tile.Frame.Y - origin.Top) * ScreenHeight, top, 6);
        Assert.Equal((tile.Frame.X + tile.Frame.Width - origin.Left) * ScreenWidth, right, 6);
        Assert.Equal((tile.Frame.Y + tile.Frame.Height - origin.Top) * ScreenHeight, bottom, 6);
    }

    /// <summary>
    /// A shape a tile draws is outlined over a coarser drawing where that drawing draws it, so the outline
    /// of a shape found on a tile is the outline the drawing would give it.
    /// </summary>
    [Fact]
    public void ATileOutlinesAShapeWhereTheDrawingDrawsIt()
    {
        var drawn = MapViewport.Anchored(MapTiling.ZoomOf(2), 0.42, 0.33, 0.5, 0.5, MapCeiling.Most);
        var drawing = Drawing(drawn);
        var tile = drawing.Tile(Tiling, Tiling.At(5, 0.42, 0.33)!.Value);
        var nodes = tile.Shapes.Where(shape => shape.IsNode).Select(shape => shape.Node).ToHashSet();
        var common = drawing.Outlines(nodes).Select(outline => outline.Node).ToHashSet();

        nodes.IntersectWith(common);
        Assert.NotEmpty(nodes);

        var expected = drawing.Outlines(nodes).ToDictionary(outline => outline.Node);

        foreach (var outline in tile.Outlines(nodes, drawn, Width, Height))
        {
            var corners = expected[outline.Node].Points;

            Assert.True(
                outline.Points.Zip(corners).All(pair => Math.Abs(pair.First.X - pair.Second.X) < Tolerance && Math.Abs(pair.First.Y - pair.Second.Y) < Tolerance),
                $"node {outline.Node} is outlined at {string.Join(", ", outline.Points)} by the tile and at {string.Join(", ", corners)} by the drawing");
        }
    }

    /// <summary>
    /// Every zoom is shown from tiles drawn at least as finely as the screen, and at most half again as
    /// finely each way, with the detail the drawing made at that zoom has.
    /// </summary>
    [Fact]
    public void EveryZoomIsShownFromTilesNeverMagnifiedWithItsOwnDetail()
    {
        var zooms = Enumerable.Range(0, 2000).Select(i => Math.Pow(2, i / 100.0))
            .Concat(Enumerable.Range(0, 30).SelectMany(power => new[] { Math.Pow(2, power), Math.Pow(2, power) * Math.Sqrt(2) }));

        foreach (var zoom in zooms)
        {
            var level = MapTiling.LevelOf(zoom);
            var drawn = MapTiling.ZoomOf(level);

            Assert.True(drawn >= zoom * (1 - 1e-12), $"a zoom of {zoom} is shown from tiles drawn at {drawn}, magnified");
            Assert.True(drawn <= zoom * Math.Sqrt(2) * (1 + 1e-12), $"a zoom of {zoom} is shown from tiles drawn at {drawn}");
            Assert.Equal(TreemapDetail.LevelsAt(zoom), MapTiling.DetailOf(level));
        }
    }

    /// <summary>Of the volume beside the root, a tile draws what the drawing does, where the drawing does.</summary>
    [Fact]
    public void ATileDrawsTheVolumeBesideTheRootAsTheDrawingDoes()
    {
        var volume = new VolumeSpace(Tree.SizeOf(Tree.RootNode) * 3, Tree.SizeOf(Tree.RootNode));
        var viewport = MapViewport.Anchored(2.5, 0.9, 0.5, 0.5, 0.5, MapCeiling.Most);
        var drawing = (TiledSurface)ExploreSurface.Create(
            Tree, Tree.RootNode, ExploreView.Treemap, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, volume, viewport);
        var level = MapTiling.LevelOf(viewport.Zoom);

        var free = drawing.Tiles.Single(shape => shape.IsFreeSpace);
        var tile = drawing.Tile(Tiling, Tiling.At(level, 0.9, 0.5)!.Value);
        var tiled = tile.Shapes.Single(shape => shape.IsFreeSpace);

        Assert.True(Same(Fraction(free, viewport), Frame(tile, tiled), Tiling.AcrossAt(level)));
    }

    /// <summary>A drawing that cannot be zoomed is not cut into tiles: it is the whole picture whatever the zoom.</summary>
    [Fact]
    public void OnlyATreemapThatZoomsIsCutIntoTiles()
    {
        var icicle = (TiledSurface)ExploreSurface.Create(
            Tree, Tree.RootNode, ExploreView.Icicle, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None);

        Assert.Throws<InvalidOperationException>(() => icicle.Tile(Tiling, new MapTileKey(0, 0, 0)));
        Assert.Throws<ArgumentException>(() => Drawing(MapViewport.Whole).Tile(new MapTiling(Width + 1, Height), new MapTileKey(0, 0, 0)));
    }

    private static TiledSurface Drawing(MapViewport viewport) => (TiledSurface)ExploreSurface.Create(
        Tree, Tree.RootNode, ExploreView.Treemap, Width, Height, scale: 1, textScale: 1,
        ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None, viewport);

    /// <summary>Every tile of <paramref name="level"/> with any of <paramref name="frame"/> of the picture in it.</summary>
    private static List<MapTile> Overlapping(TiledSurface picture, MapFrame frame, int level)
    {
        var first = Tiling.At(level, frame.X, frame.Y)!.Value;
        var last = Tiling.At(level, Math.Min(frame.X + frame.Width, 1 - 1e-12), Math.Min(frame.Y + frame.Height, 1 - 1e-12))!.Value;
        var tiles = new List<MapTile>();

        for (var row = first.Row; row <= last.Row; row++)
        {
            for (var column = first.Column; column <= last.Column; column++)
            {
                tiles.Add(picture.Tile(Tiling, new MapTileKey(level, column, row)));
            }
        }

        return tiles;
    }

    private static List<(int Node, MapFrame Frame)> InPicture(MapTile tile) =>
        [.. tile.Shapes.Where(shape => shape.IsNode).Select(shape => (shape.Node, Frame(tile, shape)))];

    private static MapFrame Frame(MapTile tile, ExploreTile shape) => tile.Frame.Inside(new MapFrame(
        shape.X / (double)tile.Width,
        shape.Y / (double)tile.Height,
        shape.Width / (double)tile.Width,
        shape.Height / (double)tile.Height));

    private static MapFrame Fraction(ExploreTile shape, MapViewport viewport) => new(
        viewport.Left + (shape.X / (Width * viewport.Zoom)),
        viewport.Top + (shape.Y / (Height * viewport.Zoom)),
        shape.Width / (Width * viewport.Zoom),
        shape.Height / (Height * viewport.Zoom));

    /// <summary>Whether two frames of the picture are the same to within <see cref="Tolerance"/> of a pixel of a picture <paramref name="across"/> pixels wide.</summary>
    private static bool Same(MapFrame a, MapFrame b, double across)
    {
        var down = across * Height / Width;

        return Math.Abs(a.X - b.X) * across < Tolerance
            && Math.Abs(a.Y - b.Y) * down < Tolerance
            && Math.Abs(a.Width - b.Width) * across < Tolerance
            && Math.Abs(a.Height - b.Height) * down < Tolerance;
    }

    private static bool Overlaps(MapFrame a, MapFrame b) =>
        a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    private static IEnumerable<(double X, double Y)> Grid(MapFrame frame) =>
        from i in Enumerable.Range(0, 40)
        from j in Enumerable.Range(0, 40)
        select (frame.X + ((i + 0.5) / 40 * frame.Width), frame.Y + ((j + 0.5) / 40 * frame.Height));

    /// <summary>
    /// Folders three deep, of files falling away in size, so each doubling of the zoom opens folders
    /// and draws files one by one that the last did not.
    /// </summary>
    private static ExploreTree Folders()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var random = new Random(277);

        Fill(ExploreTreeBuilder.RootNode, depth: 0);

        return builder.Build(ExploreChildOrder.BySize);

        void Fill(int parent, int depth)
        {
            var folders = depth < 3 ? 5 : 0;
            var children = Enumerable.Range(0, folders)
                .Select(i => new ExploreChild($"folder{i}", IsDirectory: true, IsLink: false, Size: 0))
                .Concat(Enumerable.Range(0, 60).Select(i =>
                    new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: (long)(1_000_000 * Math.Pow(0.9, i) * (1 + random.NextDouble())))))
                .ToList();

            var first = builder.AddChildren(parent, children);

            for (var i = 0; i < folders; i++)
            {
                Fill(first + i, depth + 1);
            }
        }
    }
}
