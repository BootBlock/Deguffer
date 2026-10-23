using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// A point on the screen, found in what shows there: a drawing, and where in its canvas, or a tile of
/// a drawing's picture, and where in the tile.
/// </summary>
/// <param name="Drawing">The drawing found, or the one whose picture <paramref name="Tile"/> is a piece of.</param>
/// <param name="Viewport">The part of the picture <paramref name="Drawing"/> shows.</param>
/// <param name="X">Where the point is across the drawing's canvas, or across the tile where there is one.</param>
/// <param name="Y">Where it is down it.</param>
/// <param name="Tile">The tile found, or null where the drawing itself was.</param>
/// <param name="Reading">
/// How what was found is read in the tree the page holds now, or null where it is read as it is.
/// </param>
public readonly record struct MapLocation(
    ExploreSurface Drawing, MapViewport Viewport, float X, float Y, MapTile? Tile = null, MapReading? Reading = null)
{
    /// <summary>
    /// What is at the point, read in the tree the page holds now, or null where it is over nothing or
    /// over a shape with no counterpart in that tree.
    /// </summary>
    public ExploreHit? Hit =>
        (Tile is { } tile ? tile.At(X, Y) : Drawing.At(X, Y)) is { } found
            ? Reading is { } reading ? reading.Read(found) : found
            : null;

    /// <summary>
    /// Where the shape at the point is on a screen showing <paramref name="shown"/>, in fractions of it,
    /// or null where there is no shape there or it is not a rectangle.
    /// </summary>
    public MapFrame? ShapeOnScreen(MapViewport shown)
    {
        if (Tile is { } tile)
        {
            if (tile.TileAt(X, Y) is not { } inTile)
            {
                return null;
            }

            // The whole picture is a viewport at no zoom, so its placement puts a part of the picture
            // where it is on the screen.
            return shown.PlacementOf(MapViewport.Whole).OnScreen(tile.Frame.Inside(inTile.FrameIn(tile.Width, tile.Height)));
        }

        if (Drawing.TileAt(X, Y) is not { } shape)
        {
            return null;
        }

        return shown.PlacementOf(Viewport).OnScreen(shape.FrameIn(Drawing.Width, Drawing.Height));
    }
}

/// <summary>
/// Which drawing answers for a point on the screen while a redraw is on its way.
///
/// <para>§7.1 makes this a safety question. A right-click picks what the menu then acts on, so a
/// pick resolved against a picture other than the one under the pointer is a Delete aimed at
/// something the user never pointed at. While a redraw lands, the screen shows its regions where
/// they have landed and the last picture everywhere else, so each point is answered by whichever of
/// the two shows there, and never by a drawing that is not on screen.</para>
///
/// <para>While the camera moves, tiles drawn as finely as the zoom on screen show over the last
/// drawing (see <see cref="MapTiling"/>), and they draw shapes it does not: the folders the move has
/// opened. Where one shows, it answers, so a click on a shape the tile drew picks that shape rather
/// than the folder round it. Only a redraw's landed regions show over a tile.</para>
///
/// <para>A node is a number in the tree it was drawn from, and the page reads every answer in the
/// tree it handed over last, so a number from an older tree would name whatever that number is in the
/// newer one. A drawing of an older tree of the same place, still on screen while the next one is on
/// its way, answers through <see cref="MapReading"/>: each shape with the node that is the same thing
/// in the newer tree, and nothing where none is. A drawing of anything else answers nothing.</para>
/// </summary>
public static class MapHitTest
{
    /// <summary>
    /// Find the point <paramref name="x"/>, <paramref name="y"/>, in fractions of a screen showing
    /// <paramref name="shown"/>, in what shows there. Null where what shows there cannot be read in the
    /// tree the page holds now.
    /// </summary>
    /// <param name="drawing">The last drawing that arrived, or null where none has.</param>
    /// <param name="arriving">A redraw on its way, or null.</param>
    /// <param name="readingOf">
    /// How a drawing of a tree is read in the tree the page handed over last (<see cref="MapReading.Of"/>),
    /// or null where it cannot be. Asked rather than worked out here, because the point is asked at the
    /// pointer's rate and finding nodes again is worth doing once per drawing (G5).
    /// </param>
    /// <param name="tileOver">
    /// The finest tile shown over <paramref name="drawing"/> at a point of the picture, given in
    /// fractions of it, or null where none is; or null where no tile is shown at all.
    /// </param>
    public static MapLocation? Locate(
        MapViewport shown,
        double x,
        double y,
        ExploreSurface? drawing,
        CanvasRedraw? arriving,
        Func<ISizedTree, MapReading?> readingOf,
        Func<double, double, MapTile?>? tileOver = null)
    {
        var found = arriving is { Surface: { } landing }
            && Place(shown, x, y, landing) is { } there
            && arriving.Covers(there.X, there.Y)
                ? there
                : drawing is null ? null : Over(shown, x, y, drawing, tileOver) ?? Place(shown, x, y, drawing);

        ArgumentNullException.ThrowIfNull(readingOf);

        return found is { } location && readingOf(location.Drawing.Tree) is { } reading
            ? location with { Reading = reading }
            : null;
    }

    /// <summary>
    /// The screen point in the tile shown over <paramref name="drawing"/> there, or null where none is.
    /// The tile is of the same picture as the drawing, but it may have been cut from an earlier drawing
    /// of it, so that one is what it is found in.
    /// </summary>
    private static MapLocation? Over(
        MapViewport shown, double x, double y, ExploreSurface drawing, Func<double, double, MapTile?>? tileOver)
    {
        if (tileOver is null || drawing.Viewport is not { } drawn)
        {
            return null;
        }

        var (pictureX, pictureY) = shown.PictureAt(x, y);

        if (tileOver(pictureX, pictureY) is not { } tile || !MapTiling.Over(tile.Key.Level, drawn))
        {
            return null;
        }

        var frame = tile.Frame;

        return new MapLocation(
            tile.Picture,
            tile.DrawnAt,
            (float)((pictureX - frame.X) / frame.Width * tile.Width),
            (float)((pictureY - frame.Y) / frame.Height * tile.Height),
            tile);
    }

    /// <summary>
    /// Where the screen point falls in <paramref name="drawing"/>, placed for the part of the
    /// picture it shows on a screen showing <paramref name="shown"/>, or null where the drawing does
    /// not reach it. A drawing that cannot be zoomed shows the whole picture. A point past the
    /// drawing's edge is over nothing even where a shape running off that edge would contain it:
    /// what shows there is another drawing, or nothing.
    /// </summary>
    private static MapLocation? Place(MapViewport shown, double x, double y, ExploreSurface drawing)
    {
        var viewport = drawing.Viewport ?? MapViewport.Whole;
        var (inX, inY) = shown.PlacementOf(viewport).InDrawing(x, y);

        return inX is >= 0 and < 1 && inY is >= 0 and < 1
            ? new MapLocation(drawing, viewport, (float)(inX * drawing.Width), (float)(inY * drawing.Height))
            : null;
    }
}
