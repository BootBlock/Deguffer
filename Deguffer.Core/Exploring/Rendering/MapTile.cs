using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// One tile of a treemap's picture, laid out: the shapes that reach it, in its own pixels, what a point
/// on it is over, and how it is painted. Made by <see cref="TiledSurface.Tile"/>, from the drawing whose
/// picture it is a piece of, and coloured as that drawing colours.
///
/// <para>Not an <see cref="ExploreSurface"/>. A drawing shows the part of the picture a viewport puts on
/// a canvas the screen's shape, and a tile is a square of it, so where it lies is a frame of the picture
/// rather than a viewport (<see cref="Frame"/>). It names nothing either: the names are the drawing's,
/// laid over the picture once the map is at rest.</para>
/// </summary>
public sealed class MapTile
{
    private readonly ExploreTile[] _shapes;

    private readonly TileHitTest _hits;

    private readonly Func<ExploreTile, TileColour> _colourOf;

    /// <summary>How its picture finishes each shape: see <see cref="ExploreSurface.Finish"/>.</summary>
    private readonly ShapeFinish _finish;

    internal MapTile(
        ExploreSurface picture,
        MapViewport drawnAt,
        MapTileKey key,
        MapViewport viewport,
        MapFrame frame,
        int width,
        int height,
        IReadOnlyList<ExploreTile> shapes,
        Func<ExploreTile, TileColour> colourOf,
        ShapeFinish finish)
    {
        Picture = picture;
        DrawnAt = drawnAt;
        Key = key;
        Viewport = viewport;
        Frame = frame;
        Width = width;
        Height = height;
        _shapes = shapes as ExploreTile[] ?? [.. shapes];
        _hits = new TileHitTest(_shapes, width, height);
        _colourOf = colourOf;
        _finish = finish;
    }

    /// <summary>The drawing whose picture this is a piece of.</summary>
    public ExploreSurface Picture { get; }

    /// <summary>The part of the picture <see cref="Picture"/> shows: what a point found on the tile is found in.</summary>
    public MapViewport DrawnAt { get; }

    public MapTileKey Key { get; }

    /// <summary>
    /// The viewport whose canvas, the size of <see cref="Picture"/>'s, has the tile at its corner: what
    /// it was laid out at, and so what places it (see <see cref="MapTiling.ViewportOf"/>).
    /// </summary>
    public MapViewport Viewport { get; }

    /// <summary>Where the tile lies in the whole picture, as fractions of it.</summary>
    public MapFrame Frame { get; }

    /// <summary>How wide the tile is, in its own pixels.</summary>
    public int Width { get; }

    /// <summary>How tall the tile is, in its own pixels.</summary>
    public int Height { get; }

    /// <summary>What a tile this size holds once painted, which is what the cache counts.</summary>
    public long Bytes => PixelBuffer.LengthFor(Width, Height);

    /// <summary>Every shape that reaches the tile, a folder before what is drawn of it, in the tile's pixels.</summary>
    public IReadOnlyList<ExploreTile> Shapes => _shapes;

    /// <summary>What is at this point of the tile, or null where the point is over nothing.</summary>
    public ExploreHit? At(float x, float y) =>
        _hits.At(x, y) is { } index ? new ExploreHit(_shapes[index].Node, _shapes[index].Bytes) : null;

    /// <summary>The rectangle of the shape at this point of the tile, or null where it is over nothing.</summary>
    public ExploreTile? TileAt(float x, float y) => _hits.At(x, y) is { } index ? _shapes[index] : null;

    /// <summary>What paints the tile on <paramref name="background"/>, in one region the tile's size.</summary>
    public CanvasPainter Painter(TileColour background) => new TileRasteriser(Quads(), background);

    /// <summary>The tile's shapes as the GPU shades them, or as <see cref="Painter"/> paints them.</summary>
    public CushionQuads Quads() => new(_shapes, Width, Height, _colourOf, _finish);

    /// <summary>
    /// Where the tile goes in a picture laid out over a screen <paramref name="width"/> by
    /// <paramref name="height"/> and measured from <paramref name="origin"/>: as a drawing's canvas is
    /// placed, because the tile is the corner of a canvas the size of the drawing's.
    /// </summary>
    public MapTransform Canvas(double width, double height, MapOrigin origin) =>
        Viewport.Canvas(Picture.Width, Picture.Height, width, height, origin);

    /// <summary>
    /// Where each of <paramref name="nodes"/> this tile draws lies on the canvas of a drawing
    /// <paramref name="width"/> by <paramref name="height"/> pixels made at <paramref name="drawn"/>: the
    /// outline a node the pointer found on this tile is marked with, over a drawing that may not draw it.
    /// Never an aggregate, as for <see cref="ExploreSurface.Outlines"/>.
    /// </summary>
    public IReadOnlyList<ExploreOutline> Outlines(IReadOnlySet<int> nodes, MapViewport drawn, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        // The tile's canvas placed in the drawing's, as a drawing is placed on a screen.
        var placed = drawn.PlacementOf(Viewport);
        var outlines = new List<ExploreOutline>();

        foreach (var shape in _shapes)
        {
            if (!shape.IsNode || !nodes.Contains(shape.Node))
            {
                continue;
            }

            var inDrawing = placed.OnScreen(new MapFrame(
                shape.X / (double)Picture.Width,
                shape.Y / (double)Picture.Height,
                shape.Width / (double)Picture.Width,
                shape.Height / (double)Picture.Height));

            var left = (float)(inDrawing.X * width);
            var top = (float)(inDrawing.Y * height);
            var right = (float)((inDrawing.X + inDrawing.Width) * width);
            var bottom = (float)((inDrawing.Y + inDrawing.Height) * height);

            outlines.Add(new ExploreOutline(shape.Node, [
                new ExplorePoint(left, top),
                new ExplorePoint(right, top),
                new ExplorePoint(right, bottom),
                new ExplorePoint(left, bottom),
            ]));
        }

        return outlines;
    }
}
