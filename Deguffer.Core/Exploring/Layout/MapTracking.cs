namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// A map's camera in the terms the compositor's interaction tracker holds it in: the picture
/// magnified <paramref name="Scale"/> times, and moved so the screen's top-left corner is
/// (<paramref name="X"/>, <paramref name="Y"/>) pixels from <paramref name="Origin"/> in the
/// magnified picture.
///
/// <para>The tracker is what moves the camera, so a pinch, a fling and a turn of the wheel with Ctrl
/// held run on the compositor with nothing on the UI thread. <see cref="MapViewport"/> stays the one account of what
/// the screen shows: this is the same thing in the tracker's units, and every viewport the map works
/// from is read back through <see cref="Shown"/>. Pixels rather than fractions because the tracker
/// keeps its bounds, its inertia and its velocities in them.</para>
///
/// <para>The picture is laid out unmagnified over the screen, so at scale 1 it is exactly the screen.
/// Measured from the picture's corner, the tracker can move from (0, 0) to ((scale - 1) × width,
/// (scale - 1) × height). Measured from an origin near what is shown, those bounds move by the
/// origin and the position stays small, which the single-precision tracker needs at a deep zoom
/// (see <see cref="MapOrigin"/>). A scale about a point is the same rule from any origin, so a pinch
/// keeps what is under the hand under it whatever the origin is.</para>
/// </summary>
public readonly record struct MapTracking(double X, double Y, double Scale, MapOrigin Origin)
{
    /// <summary>
    /// How many screens away a still point of a move may be before the move is played as a pan. A
    /// zoom that barely changes has its still point far off the screen, and the tracker keeps
    /// positions in single precision, where a point much further off than this could no longer be
    /// placed to a fraction of a pixel.
    /// </summary>
    public const double PivotReach = 64;

    /// <summary>
    /// Where the tracker holds <paramref name="viewport"/> on a screen <paramref name="width"/> by
    /// <paramref name="height"/>, measured from <paramref name="origin"/>.
    /// </summary>
    public static MapTracking Of(MapViewport viewport, double width, double height, MapOrigin origin) => new(
        (viewport.Left - origin.Left) * viewport.Zoom * width,
        (viewport.Top - origin.Top) * viewport.Zoom * height,
        viewport.Zoom,
        origin);

    /// <summary>
    /// What a screen <paramref name="width"/> by <paramref name="height"/> shows with the tracker here.
    ///
    /// <para>Where the picture may be stretched past its limits, <paramref name="elastic"/>, the screen
    /// shows the tracker as it is, a pinch past the zoom's limits and a pan past the picture's edge
    /// included. Where it may not, because the reader has turned animation effects off and a spring
    /// back is movement, the camera follows the tracker held to its limits, the zoom first, up to
    /// <paramref name="ceiling"/>, and then the position at that zoom, and the screen shows that. The
    /// camera's expressions on the compositor do the same arithmetic, and the two have to agree,
    /// because a click is resolved through this (§7.1).</para>
    /// </summary>
    public MapViewport Shown(double width, double height, bool elastic, double ceiling)
    {
        var shown = elastic ? this : Held(width, height, ceiling);

        return MapViewport.Seen(
            shown.Scale,
            Origin.Left + (shown.X / (shown.Scale * width)),
            Origin.Top + (shown.Y / (shown.Scale * height)));
    }

    /// <summary>
    /// This held to the tracker's limits on a screen <paramref name="width"/> by
    /// <paramref name="height"/>: the scale between 1 and <paramref name="ceiling"/>, then the position
    /// between the picture's edges at that scale.
    ///
    /// <para>Held here in pixels rather than by reading this back as a viewport and holding that,
    /// because the two differ for a scale past its limits: a viewport's edges are fractions of the
    /// picture at the scale it had, so holding the scale would move the position with it. The camera's
    /// expression on the compositor holds the tracker's own pixels, as this does, and a click is
    /// resolved through this, so the two must agree (§7.1).</para>
    /// </summary>
    public MapTracking Held(double width, double height, double ceiling)
    {
        var scale = Math.Clamp(Scale, 1, ceiling);
        var (left, right) = Origin.Across(scale, width);
        var (top, bottom) = Origin.Down(scale, height);

        return new MapTracking(Math.Clamp(X, left, right), Math.Clamp(Y, top, bottom), scale, Origin);
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
        if (MapViewport.SameZoom(from, to))
        {
            return null;
        }

        var shrink = (1 / from.Zoom) - (1 / to.Zoom);
        var x = (to.Left - from.Left) / shrink;
        var y = (to.Top - from.Top) / shrink;

        return Math.Abs(x) > PivotReach || Math.Abs(y) > PivotReach ? null : (x * width, y * height);
    }
}
