using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One tile of a treemap's picture: the square <paramref name="Column"/> along and <paramref name="Row"/>
/// down at <paramref name="Level"/>, in the grid <see cref="MapTiling"/> lays over the picture. Long,
/// because the picture at the deepest zoom is trillions of pixels across.
/// </summary>
public readonly record struct MapTileKey(int Level, long Column, long Row);

/// <summary>
/// The tiles a treemap's picture is cut into while the map moves, so what comes into view is drawn as
/// sharply as the zoom it is seen at, a tile at a time, rather than only once the move stops.
///
/// <para>A level for each half of a doubling of the zoom. Each level shows the detail every zoom in its
/// half shows, which is the detail of that doubling (see <see cref="TreemapDetail"/>), drawn as finely as
/// the deepest zoom in the half. So a tile is never magnified on screen, and holds at most twice as many
/// pixels as the part of the screen it covers. A level per doubling would hold up to four times as many,
/// and one drawn at the least zoom of its half would be magnified, which is the blur this is for.</para>
///
/// <para>Because a treemap lays every folder out once, at the first level of detail where it fits, every
/// shape a tile draws is drawn in the same place by every tile of a deeper level, and only magnified. So
/// tiles of different levels agree where they meet, which is what lets them be shown together.</para>
///
/// <para>The picture is the one a drawing of the whole screen shows, on a canvas of
/// <see cref="Width"/> by <see cref="Height"/> device pixels: its tiles are pieces of that picture, not
/// pictures of their own (G1: what is in each tile is <see cref="MapTile"/>'s).</para>
/// </summary>
public sealed class MapTiling
{
    /// <summary>
    /// How wide and how tall a tile is, in its own pixels. A painted region's size, so a tile is painted
    /// in one region and put on screen in one write (see <see cref="PaintOrder.RegionSize"/>).
    /// </summary>
    public const int Size = PaintOrder.RegionSize;

    /// <param name="width">The width of the canvas the picture is drawn on at rest, in device pixels.</param>
    /// <param name="height">Its height.</param>
    public MapTiling(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// The level whose tiles a picture magnified <paramref name="zoom"/> times is shown from: the one whose
    /// half of a doubling the zoom is in. Taken from the same logarithm <see cref="TreemapDetail"/> counts
    /// its levels by, so the two never disagree on which doubling a zoom is in.
    /// </summary>
    public static int LevelOf(double zoom) => Math.Max(0, (int)Math.Floor(2 * Math.Log2(zoom)));

    /// <summary>How many times the picture is magnified in the tiles of <paramref name="level"/>: the deepest zoom they are shown at.</summary>
    public static double ZoomOf(int level) => Math.Pow(2, (level + 1) / 2.0);

    /// <summary>How many levels of detail the tiles of <paramref name="level"/> open: those of the doubling it is half of.</summary>
    public static int DetailOf(int level) => (level / 2) + 1;

    /// <summary>
    /// Whether the tiles of <paramref name="level"/> show over a drawing made at <paramref name="drawn"/>,
    /// rather than under it: drawn more finely than it, which is never with less detail. The one rule the
    /// tiles are stacked by and a point on the screen is found by, so what answers a point is what shows
    /// there (§7.1).
    /// </summary>
    public static bool Over(int level, MapViewport drawn) => ZoomOf(level) > drawn.Zoom;

    /// <summary>How many device pixels across the whole picture is at <paramref name="level"/>.</summary>
    public double AcrossAt(int level) => Width * ZoomOf(level);

    /// <summary>How many device pixels down the whole picture is at <paramref name="level"/>.</summary>
    public double DownAt(int level) => Height * ZoomOf(level);

    /// <summary>
    /// The size of the tile in its own pixels: <see cref="Size"/> each way, or less in the last column
    /// and the last row, where the picture ends.
    /// </summary>
    public (int Width, int Height) PixelsOf(MapTileKey key) => (
        Extent(AcrossAt(key.Level), key.Column),
        Extent(DownAt(key.Level), key.Row));

    /// <summary>Where the tile lies in the whole picture, as fractions of it.</summary>
    public MapFrame FrameOf(MapTileKey key)
    {
        var across = AcrossAt(key.Level);
        var down = DownAt(key.Level);
        var (width, height) = PixelsOf(key);

        return new MapFrame(key.Column * (double)Size / across, key.Row * (double)Size / down, width / across, height / down);
    }

    /// <summary>
    /// The viewport whose canvas has the tile at its corner, at the tile's zoom: what the tile is laid out
    /// at. Not held inside the picture, because a tile in the last column has the edge of the picture
    /// inside it rather than at the canvas's.
    /// </summary>
    public MapViewport ViewportOf(MapTileKey key)
    {
        var frame = FrameOf(key);

        return MapViewport.Seen(ZoomOf(key.Level), frame.X, frame.Y);
    }

    /// <summary>The tile of <paramref name="level"/> holding picture point (<paramref name="x"/>, <paramref name="y"/>), in fractions of the picture; null outside it.</summary>
    public MapTileKey? At(int level, double x, double y)
    {
        if (x is < 0 or >= 1 || y is < 0 or >= 1)
        {
            return null;
        }

        return new MapTileKey(
            level,
            (long)Math.Floor(x * AcrossAt(level) / Size),
            (long)Math.Floor(y * DownAt(level) / Size));
    }

    /// <summary>
    /// Every tile of <paramref name="level"/> with any of <paramref name="shown"/> in it, the one holding
    /// picture point (<paramref name="focusX"/>, <paramref name="focusY"/>) first and the rest by how far
    /// their middles are from it: the order a reader looking there wants them drawn in.
    /// </summary>
    public IReadOnlyList<MapTileKey> Covering(MapViewport shown, int level, double focusX, double focusY)
    {
        var across = AcrossAt(level);
        var down = DownAt(level);
        var span = 1 / shown.Zoom;

        var (firstColumn, lastColumn) = Range(shown.Left, span, across);
        var (firstRow, lastRow) = Range(shown.Top, span, down);

        var keys = new List<MapTileKey>();
        var distances = new List<double>();

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var key = new MapTileKey(level, column, row);
                var (x, y) = FrameOf(key).Centre;

                keys.Add(key);
                distances.Add(Squared((x - focusX) * across) + Squared((y - focusY) * down));
            }
        }

        var ordered = keys.ToArray();
        Array.Sort(distances.ToArray(), ordered);

        return ordered;

        static double Squared(double value) => value * value;
    }

    /// <summary>The first and last tile along one axis with any of the part from <paramref name="start"/> for <paramref name="span"/> in it.</summary>
    private static (long First, long Last) Range(double start, double span, double pixels)
    {
        var last = (long)Math.Ceiling(pixels / Size) - 1;
        var first = (long)Math.Floor(Math.Clamp(start, 0, 1) * pixels / Size);
        var end = (long)Math.Ceiling(Math.Clamp(start + span, 0, 1) * pixels / Size) - 1;

        return (Math.Clamp(first, 0, last), Math.Clamp(end, 0, last));
    }

    /// <summary>How many pixels of a picture <paramref name="pixels"/> across the tile at <paramref name="index"/> along it has.</summary>
    private static int Extent(double pixels, long index) =>
        (int)Math.Clamp(Math.Ceiling(pixels - (index * (double)Size)), 1, Size);
}
