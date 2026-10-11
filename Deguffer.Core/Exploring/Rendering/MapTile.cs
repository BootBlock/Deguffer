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

    private readonly Func<int, int, TileColour> _colourOf;

    internal MapTile(
        ExploreSurface picture,
        MapTileKey key,
        MapFrame frame,
        int width,
        int height,
        IReadOnlyList<ExploreTile> shapes,
        Func<int, int, TileColour> colourOf)
    {
        Picture = picture;
        Key = key;
        Frame = frame;
        Width = width;
        Height = height;
        _shapes = shapes as ExploreTile[] ?? [.. shapes];
        _hits = new TileHitTest(_shapes, width, height);
        _colourOf = colourOf;
    }

    /// <summary>The drawing whose picture this is a piece of.</summary>
    public ExploreSurface Picture { get; }

    /// <summary>The tree drawn. A node is a number in it, and means nothing in another.</summary>
    public ISizedTree Tree => Picture.Tree;

    public MapTileKey Key { get; }

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
    public CanvasPainter Painter(TileColour background) =>
        new TileRasteriser(_shapes, Width, Height, background, _colourOf);

    /// <summary>
    /// Where each of <paramref name="nodes"/> this tile draws lies on the canvas of a drawing
    /// <paramref name="width"/> by <paramref name="height"/> pixels made at <paramref name="drawn"/>: the
    /// outline a node the pointer found on this tile is marked with, over a drawing that may not draw it.
    /// Never an aggregate, as for <see cref="ExploreSurface.Outlines"/>.
    /// </summary>
    public IReadOnlyList<ExploreOutline> Outlines(IReadOnlySet<int> nodes, MapViewport drawn, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        // A tile pixel is a fraction of the picture its frame says, and a drawing's canvas pixel the
        // fraction its viewport says: one scale and one offset on each axis between the two.
        var scaleX = Frame.Width / Width * drawn.Zoom * width;
        var scaleY = Frame.Height / Height * drawn.Zoom * height;
        var offsetX = (Frame.X - drawn.Left) * drawn.Zoom * width;
        var offsetY = (Frame.Y - drawn.Top) * drawn.Zoom * height;

        var outlines = new List<ExploreOutline>();

        foreach (var shape in _shapes)
        {
            if (!shape.IsNode || !nodes.Contains(shape.Node))
            {
                continue;
            }

            var left = (float)(offsetX + (shape.X * scaleX));
            var top = (float)(offsetY + (shape.Y * scaleY));
            var right = (float)(offsetX + ((shape.X + shape.Width) * scaleX));
            var bottom = (float)(offsetY + ((shape.Y + shape.Height) * scaleY));

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
