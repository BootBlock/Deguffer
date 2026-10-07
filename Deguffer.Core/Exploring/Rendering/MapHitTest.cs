using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// A point on the screen, found in the drawing that shows there: which drawing, and where in its
/// canvas.
/// </summary>
/// <param name="Viewport">The part of the picture <paramref name="Drawing"/> shows.</param>
public readonly record struct MapLocation(ExploreSurface Drawing, MapViewport Viewport, float X, float Y);

/// <summary>
/// Which drawing answers for a point on the screen while a redraw is on its way.
///
/// <para>§7.1 makes this a safety question. A right-click picks what the menu then acts on, so a
/// pick resolved against a picture other than the one under the pointer is a Delete aimed at
/// something the user never pointed at. While a redraw lands, the screen shows its regions where
/// they have landed and the last picture everywhere else, so each point is answered by whichever of
/// the two shows there, and never by a drawing that is not on screen.</para>
///
/// <para>A drawing of another tree answers nothing. A node is a number in the tree it was drawn
/// from, and the page reads every answer in the tree it handed over last, so a number from an older
/// tree would name whatever that number is in the newer one.</para>
/// </summary>
public static class MapHitTest
{
    /// <summary>
    /// Find the point <paramref name="x"/>, <paramref name="y"/>, in fractions of a screen showing
    /// <paramref name="shown"/>, in the drawing that shows there. Null where no drawing of
    /// <paramref name="tree"/> reaches it.
    /// </summary>
    /// <param name="drawing">The last drawing that arrived, or null where none has.</param>
    /// <param name="arriving">A redraw on its way, or null.</param>
    /// <param name="tree">The tree the page handed over last.</param>
    public static MapLocation? Locate(
        MapViewport shown,
        double x,
        double y,
        ExploreSurface? drawing,
        CanvasRedraw? arriving,
        ISizedTree? tree)
    {
        var found = arriving is { Surface: { } landing }
            && Place(shown, x, y, landing) is { } there
            && arriving.Covers(there.X, there.Y)
                ? there
                : drawing is null ? null : Place(shown, x, y, drawing);

        return found is { } location && ReferenceEquals(location.Drawing.Tree, tree) ? location : null;
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
