namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// A map's camera in the terms the compositor's interaction tracker holds it in: the picture
/// magnified <paramref name="Scale"/> times, and moved so the screen's top-left corner is
/// (<paramref name="X"/>, <paramref name="Y"/>) pixels into the magnified picture.
///
/// <para>The tracker is what moves the camera, so a pinch, a fling and a turn of the wheel run on the
/// compositor with nothing on the UI thread. <see cref="MapViewport"/> stays the one account of what
/// the screen shows: this is the same thing in the tracker's units, and every viewport the map works
/// from is read back through <see cref="Shown"/>. Pixels rather than fractions because the tracker
/// keeps its bounds, its inertia and its velocities in them.</para>
///
/// <para>The picture is laid out unmagnified over the screen, so at scale 1 it is exactly the screen,
/// and the tracker can move from (0, 0) to ((scale - 1) × width, (scale - 1) × height).</para>
/// </summary>
public readonly record struct MapTracking(double X, double Y, double Scale)
{
    /// <summary>
    /// How many screens away a still point of a move may be before the move is played as a pan. A
    /// zoom that barely changes has its still point far off the screen, and the tracker keeps
    /// positions in single precision, where a point much further off than this could no longer be
    /// placed to a fraction of a pixel.
    /// </summary>
    public const double PivotReach = 64;

    /// <summary>Where the tracker holds <paramref name="viewport"/> on a screen <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static MapTracking Of(MapViewport viewport, double width, double height) => new(
        viewport.Left * viewport.Zoom * width,
        viewport.Top * viewport.Zoom * height,
        viewport.Zoom);

    /// <summary>
    /// What a screen <paramref name="width"/> by <paramref name="height"/> shows with the tracker here.
    ///
    /// <para>Where the picture may be stretched past its limits, <paramref name="elastic"/>, the screen
    /// shows the tracker as it is, a pinch past the zoom's limits and a pan past the picture's edge
    /// included. Where it may not, because the reader has turned animation effects off and a spring
    /// back is movement, the camera follows the tracker held to its limits, the zoom first and then the
    /// position at that zoom, and the screen shows that. The camera's expressions on the compositor do
    /// the same arithmetic, and the two have to agree, because a click is resolved through this (§7.1).</para>
    /// </summary>
    public MapViewport Shown(double width, double height, bool elastic)
    {
        var shown = elastic ? this : Held(width, height);

        return MapViewport.Seen(shown.Scale, shown.X / (shown.Scale * width), shown.Y / (shown.Scale * height));
    }

    /// <summary>
    /// This held to the tracker's limits on a screen <paramref name="width"/> by
    /// <paramref name="height"/>: the scale between 1 and <see cref="MapViewport.MaximumZoom"/>, then
    /// the position between the picture's edges at that scale.
    /// </summary>
    public MapTracking Held(double width, double height)
    {
        var scale = Math.Clamp(Scale, 1, MapViewport.MaximumZoom);

        return new MapTracking(
            Math.Clamp(X, 0, (scale - 1) * width),
            Math.Clamp(Y, 0, (scale - 1) * height),
            scale);
    }

    /// <summary>
    /// The point on a screen <paramref name="width"/> by <paramref name="height"/>, in pixels, that
    /// stays where it is as the camera moves from <paramref name="from"/> to <paramref name="to"/>.
    /// Null where the zoom does not change, or changes so little that the point is more than
    /// <see cref="PivotReach"/> screens away, and the move is a pan.
    ///
    /// <para>A zoom about this point is the move <see cref="MapViewport.Between"/> describes: every
    /// point the two viewports agree on stays still throughout. So the tracker, which keeps the point
    /// it is asked to scale about where it is, plays the whole move from a scale and this point alone,
    /// and the position follows exactly.</para>
    /// </summary>
    public static (double X, double Y)? Pivot(MapViewport from, MapViewport to, double width, double height)
    {
        // Compared with a tolerance for the reason MapViewport.Between does.
        if (Math.Abs((to.Zoom / from.Zoom) - 1) < 1e-9)
        {
            return null;
        }

        var shrink = (1 / from.Zoom) - (1 / to.Zoom);
        var x = (to.Left - from.Left) / shrink;
        var y = (to.Top - from.Top) / shrink;

        return Math.Abs(x) > PivotReach || Math.Abs(y) > PivotReach ? null : (x * width, y * height);
    }
}
