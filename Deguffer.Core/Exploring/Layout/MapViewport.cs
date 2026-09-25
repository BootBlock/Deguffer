namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Which part of a drawing is on screen: how far the whole picture is magnified, and where the
/// screen's top-left corner falls in it.
///
/// <para>Measured in fractions of the whole picture rather than in pixels, so a zoomed map that is
/// resized goes on showing the same part of the tree, and a drawing made at one viewport can be
/// placed exactly on a screen showing another — which is how a zoom animates over a picture that is
/// only redrawn once it stops (see <see cref="PlacementOf"/>).</para>
///
/// <para>The default is the whole picture, as <see cref="Whole"/> is. The zoom is held as how far it
/// is beyond one so that a viewport nobody set is a meaningful one rather than a zoom of nothing.</para>
/// </summary>
public readonly record struct MapViewport
{
    /// <summary>
    /// How far a picture can be magnified.
    ///
    /// <para>Sixty-four times is more detail than the picture can use: the smallest shape a treemap
    /// draws is three pixels, so at this zoom a 4K canvas has room for a shape standing for a few
    /// hundred bytes of a terabyte volume. Past that the list view reads the same data better.</para>
    ///
    /// <para>It is also the precision limit. A shape is laid out in double precision and handed over
    /// in single precision, relative to the screen, and a shape that runs far off it has a visible edge
    /// added up from two large numbers. At this zoom a 4K picture is a quarter of a million pixels
    /// across, where a single-precision step is a sixty-fourth of a pixel, so an edge is out by a few
    /// hundredths of a pixel at most. Much further and the error would start to show as seams.</para>
    /// </summary>
    public const double MaximumZoom = 64;

    private readonly double _beyond;

    private MapViewport(double zoom, double left, double top)
    {
        _beyond = zoom - 1;
        Left = left;
        Top = top;
    }

    /// <summary>The whole picture, unmagnified.</summary>
    public static MapViewport Whole => default;

    /// <summary>How many times the whole picture is magnified: 1, which shows all of it, to <see cref="MaximumZoom"/>.</summary>
    public double Zoom => 1 + _beyond;

    /// <summary>Where the screen's left edge falls, as a fraction of the whole picture's width.</summary>
    public double Left { get; }

    /// <summary>Where the screen's top edge falls, as a fraction of the whole picture's height.</summary>
    public double Top { get; }

    /// <summary>Whether this shows the whole picture.</summary>
    public bool IsWhole => _beyond == 0;

    /// <summary>
    /// The viewport at <paramref name="zoom"/> that shows picture point
    /// (<paramref name="pictureX"/>, <paramref name="pictureY"/>) at screen point
    /// (<paramref name="screenX"/>, <paramref name="screenY"/>), all four as fractions.
    ///
    /// <para>This is zooming at the pointer: the thing under it stays under it. The zoom is held
    /// between 1 and <see cref="MaximumZoom"/>, and the position is then held inside the picture, so
    /// near an edge the thing under the pointer moves as far as it has to and no further. A screen
    /// never shows past the picture's edge, because there is nothing there to show.</para>
    /// </summary>
    public static MapViewport Anchored(double zoom, double pictureX, double pictureY, double screenX, double screenY)
    {
        zoom = Math.Clamp(zoom, 1, MaximumZoom);

        return Within(zoom, pictureX - (screenX / zoom), pictureY - (screenY / zoom));
    }

    /// <summary>
    /// The viewport that shows all of <paramref name="part"/> of the picture as large as the screen
    /// allows, centred on it.
    ///
    /// <para>The zoom is one factor on both axes, so a part that is not the screen's shape fills it one
    /// way and leaves room beside it the other. Stretching it to fill both would draw every shape in it
    /// a different shape from the one it has. A part smaller than the maximum zoom can show whole is
    /// shown at the maximum, still centred, and a part at an edge is held inside the picture like any
    /// other viewport.</para>
    /// </summary>
    public static MapViewport Fitting(MapFrame part)
    {
        var zoom = Math.Clamp(Math.Min(1 / part.Width, 1 / part.Height), 1, MaximumZoom);
        var (centreX, centreY) = part.Centre;

        return Within(zoom, centreX - (0.5 / zoom), centreY - (0.5 / zoom));
    }

    /// <summary>
    /// This viewport moved with a hand dragging the picture by (<paramref name="screenX"/>,
    /// <paramref name="screenY"/>) of the screen, so the part that was under the hand stays under it.
    ///
    /// <para>Held inside the picture, so a drag past an edge stops there and the part under the hand
    /// slips as far as it has to, as it does for a zoom at an edge.</para>
    /// </summary>
    public MapViewport Panned(double screenX, double screenY) =>
        Within(Zoom, Left - (screenX / Zoom), Top - (screenY / Zoom));

    /// <summary>
    /// The viewport a fraction <paramref name="progress"/> of the way from <paramref name="from"/> to
    /// <paramref name="to"/>.
    ///
    /// <para>The zoom moves by equal ratios rather than equal steps, so a zoom from 1 to 8 spends as
    /// long doubling from 4 to 8 as from 1 to 2 — which is what reads as a steady speed. The position
    /// moves in step with how much of the picture is on screen, which is the one path along which
    /// every point the two viewports agree on stays still. A zoom at the pointer is exactly that, so
    /// the thing under the pointer stays under it throughout rather than only at the two ends.</para>
    ///
    /// <para>Every step is inside the picture if both ends are. Each edge is a straight line in how
    /// much of the picture is shown, and so is the limit on it.</para>
    /// </summary>
    public static MapViewport Between(MapViewport from, MapViewport to, double progress)
    {
        if (progress <= 0)
        {
            return from;
        }

        if (progress >= 1)
        {
            return to;
        }

        var ratio = to.Zoom / from.Zoom;

        // At one zoom the path above has no length in how much is shown, so the two positions are
        // simply blended. Compared with a tolerance because a zoom that went up a step and back down
        // again arrives a rounding error away from where it started.
        if (Math.Abs(ratio - 1) < 1e-9)
        {
            return Within(
                from.Zoom,
                from.Left + ((to.Left - from.Left) * progress),
                from.Top + ((to.Top - from.Top) * progress));
        }

        var zoom = from.Zoom * Math.Pow(ratio, progress);
        var along = ((1 / zoom) - (1 / from.Zoom)) / ((1 / to.Zoom) - (1 / from.Zoom));

        return Within(
            zoom,
            from.Left + ((to.Left - from.Left) * along),
            from.Top + ((to.Top - from.Top) * along));
    }

    /// <summary>Where screen point (<paramref name="screenX"/>, <paramref name="screenY"/>) falls in the whole picture.</summary>
    public (double X, double Y) PictureAt(double screenX, double screenY) =>
        (Left + (screenX / Zoom), Top + (screenY / Zoom));

    /// <summary>Where <paramref name="onScreen"/>, a part of the screen, falls in the whole picture.</summary>
    public MapFrame PictureOf(MapFrame onScreen)
    {
        var (x, y) = PictureAt(onScreen.X, onScreen.Y);

        return new MapFrame(x, y, onScreen.Width / Zoom, onScreen.Height / Zoom);
    }

    /// <summary>
    /// Where a drawing made at <paramref name="drawn"/> sits on a screen showing this viewport.
    ///
    /// <para>What lets a zoom animate without drawing a frame of it. The drawing on hand is moved and
    /// magnified to where its shapes belong in this viewport, which is the right picture at the wrong
    /// resolution and costs nothing to show, and it is drawn again only once the zoom stops.</para>
    /// </summary>
    public MapPlacement PlacementOf(MapViewport drawn) => new(
        Zoom / drawn.Zoom,
        (drawn.Left - Left) * Zoom,
        (drawn.Top - Top) * Zoom);

    /// <summary>
    /// A viewport held inside the picture. The zoom is held as well, because an eased step between
    /// two zooms can land a rounding error outside them, and a zoom a hair under 1 would leave no room
    /// between the edges to hold the position in.
    /// </summary>
    private static MapViewport Within(double zoom, double left, double top)
    {
        zoom = Math.Clamp(zoom, 1, MaximumZoom);

        var span = 1 / zoom;

        return new MapViewport(zoom, Math.Clamp(left, 0, 1 - span), Math.Clamp(top, 0, 1 - span));
    }
}

/// <summary>
/// Where a drawing sits on the screen, as fractions of the screen: magnified by
/// <paramref name="Scale"/> and moved so its top-left corner is at (<paramref name="X"/>,
/// <paramref name="Y"/>).
/// </summary>
public readonly record struct MapPlacement(double Scale, double X, double Y)
{
    /// <summary>
    /// Where screen point (<paramref name="x"/>, <paramref name="y"/>) falls in the drawing, as a
    /// fraction of it. Outside 0 to 1 where the drawing does not reach that point.
    ///
    /// <para>What a click is resolved through while a zoom is still moving. What the pointer is over
    /// has to be the shape the screen shows there, because a right-click picks what the menu then acts
    /// on (§7.1), and the screen is showing this placement rather than the drawing as it was made.</para>
    /// </summary>
    public (double X, double Y) InDrawing(double x, double y) => ((x - X) / Scale, (y - Y) / Scale);

    /// <summary>Where <paramref name="inDrawing"/>, a part of the drawing, is on the screen.</summary>
    public MapFrame OnScreen(MapFrame inDrawing) => new(
        X + (inDrawing.X * Scale),
        Y + (inDrawing.Y * Scale),
        inDrawing.Width * Scale,
        inDrawing.Height * Scale);
}
