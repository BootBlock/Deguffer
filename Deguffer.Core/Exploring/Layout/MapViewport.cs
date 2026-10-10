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
    private readonly double _beyond;

    private MapViewport(double zoom, double left, double top)
    {
        _beyond = zoom - 1;
        Left = left;
        Top = top;
    }

    /// <summary>The whole picture, unmagnified.</summary>
    public static MapViewport Whole => default;

    /// <summary>How many times the whole picture is magnified: 1, which shows all of it, to the map's ceiling (<see cref="MapCeiling"/>).</summary>
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
    /// between 1 and <paramref name="ceiling"/>, and the position is then held inside the picture, so
    /// near an edge the thing under the pointer moves as far as it has to and no further. A screen
    /// never shows past the picture's edge, because there is nothing there to show.</para>
    /// </summary>
    public static MapViewport Anchored(double zoom, double pictureX, double pictureY, double screenX, double screenY, double ceiling)
    {
        zoom = Math.Clamp(zoom, 1, ceiling);

        return Within(zoom, pictureX - (screenX / zoom), pictureY - (screenY / zoom));
    }

    /// <summary>
    /// The viewport that shows all of <paramref name="part"/> of the picture as large as the screen
    /// allows, centred on it.
    ///
    /// <para>The zoom is one factor on both axes, so a part that is not the screen's shape fills it one
    /// way and leaves room beside it the other. Stretching it to fill both would draw every shape in it
    /// a different shape from the one it has. A part smaller than <paramref name="ceiling"/> can show
    /// whole is shown at the ceiling, still centred, and a part at an edge is held inside the picture
    /// like any other viewport.</para>
    /// </summary>
    public static MapViewport Fitting(MapFrame part, double ceiling)
    {
        var zoom = Math.Clamp(Math.Min(1 / part.Width, 1 / part.Height), 1, ceiling);
        var (centreX, centreY) = part.Centre;

        return Within(zoom, centreX - (0.5 / zoom), centreY - (0.5 / zoom));
    }

    /// <summary>
    /// This viewport moved with a hand dragging the picture by (<paramref name="screenX"/>,
    /// <paramref name="screenY"/>) of the screen, so the part that was under the hand stays under it.
    ///
    /// <para>Held inside the picture, so a drag past an edge stops there and the part under the hand
    /// slips as far as it has to, as it does for a zoom at an edge. The zoom is not held: a pan never
    /// changes it.</para>
    /// </summary>
    public MapViewport Panned(double screenX, double screenY) =>
        Within(Zoom, Left - (screenX / Zoom), Top - (screenY / Zoom));

    /// <summary>
    /// This viewport magnified <paramref name="factor"/> times more about screen point
    /// (<paramref name="screenX"/>, <paramref name="screenY"/>), so the thing there stays there, as
    /// far as the picture's edges, 1 and <paramref name="ceiling"/> allow. See <see cref="Anchored"/>.
    /// </summary>
    public MapViewport ZoomedAt(double factor, double screenX, double screenY, double ceiling)
    {
        var (pictureX, pictureY) = PictureAt(screenX, screenY);

        return Anchored(Zoom * factor, pictureX, pictureY, screenX, screenY, ceiling);
    }

    /// <summary>
    /// What a screen shows while a hand stretches the picture: magnified <paramref name="zoom"/> times
    /// with its left and top edges at <paramref name="left"/> and <paramref name="top"/>, held to
    /// nothing. A pinch can take the zoom a little past its limits and a pan can take the screen a
    /// little past the picture's edge, and the picture springs back from there once it is let go
    /// (<see cref="Held"/>), but until then a click is resolved against what the screen shows (§7.1).
    /// </summary>
    public static MapViewport Seen(double zoom, double left, double top)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(zoom);

        return new MapViewport(zoom, left, top);
    }

    /// <summary>
    /// Where this comes to rest: the zoom held between 1 and <paramref name="ceiling"/>, and the screen
    /// held inside the picture. Itself for any viewport made by anything but <see cref="Seen"/>, at a
    /// zoom the ceiling has not since come down below.
    /// </summary>
    public MapViewport Held(double ceiling) => Within(Math.Clamp(Zoom, 1, ceiling), Left, Top);

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
    /// much of the picture is shown, and so is the limit on it. The zoom is held between the two ends,
    /// because an eased step between them can land a rounding error outside them.</para>
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

        // At one zoom the path above has no length in how much is shown, so the two positions are
        // simply blended.
        if (SameZoom(from, to))
        {
            return Within(
                from.Zoom,
                from.Left + ((to.Left - from.Left) * progress),
                from.Top + ((to.Top - from.Top) * progress));
        }

        var zoom = Math.Clamp(
            from.Zoom * Math.Pow(to.Zoom / from.Zoom, progress),
            Math.Min(from.Zoom, to.Zoom),
            Math.Max(from.Zoom, to.Zoom));
        var along = ((1 / zoom) - (1 / from.Zoom)) / ((1 / to.Zoom) - (1 / from.Zoom));

        return Within(
            zoom,
            from.Left + ((to.Left - from.Left) * along),
            from.Top + ((to.Top - from.Top) * along));
    }

    /// <summary>
    /// Whether a move from <paramref name="from"/> to <paramref name="to"/> keeps one zoom, and so
    /// has no still point to zoom about. Compared with a tolerance because a zoom that went up a step
    /// and back down again arrives a rounding error away from where it started.
    /// </summary>
    internal static bool SameZoom(MapViewport from, MapViewport to) => Math.Abs((to.Zoom / from.Zoom) - 1) < 1e-9;

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
    /// Where the whole picture, laid out unmagnified over a screen <paramref name="width"/> by
    /// <paramref name="height"/> with <paramref name="origin"/> at the screen's corner, goes on a
    /// screen showing this viewport: the map's camera.
    ///
    /// <para>The same placement as <see cref="PlacementOf"/>, split in two so a drawing is placed in
    /// the picture once (<see cref="Canvas"/>) and only the camera changes as the picture moves. Both
    /// halves are measured from the same origin, so each stays small near it (see
    /// <see cref="MapOrigin"/>), and the origin cancels between them.</para>
    /// </summary>
    public MapTransform Camera(double width, double height, MapOrigin origin) =>
        new(Zoom, Zoom, -(Left - origin.Left) * Zoom * width, -(Top - origin.Top) * Zoom * height);

    /// <summary>
    /// Where a canvas <paramref name="canvasWidth"/> by <paramref name="canvasHeight"/> pixels across,
    /// drawn at this viewport, lies in the whole picture laid out unmagnified over a screen
    /// <paramref name="width"/> by <paramref name="height"/> with <paramref name="origin"/> at the
    /// screen's corner.
    ///
    /// <para>Scaled on each axis apart, because the canvas and the screen part company while a resize
    /// settles: the canvas is drawn for the old size and stretched over the new one.</para>
    /// </summary>
    public MapTransform Canvas(int canvasWidth, int canvasHeight, double width, double height, MapOrigin origin) => new(
        width / canvasWidth / Zoom,
        height / canvasHeight / Zoom,
        (Left - origin.Left) * width,
        (Top - origin.Top) * height);

    /// <summary>
    /// Where the labels of a canvas <paramref name="canvasWidth"/> by <paramref name="canvasHeight"/>
    /// pixels across lie in the whole picture: <see cref="Canvas"/>, for labels laid out in
    /// device-independent pixels of a canvas drawn at <paramref name="scale"/> pixels to each.
    /// </summary>
    public MapTransform Labels(int canvasWidth, int canvasHeight, double width, double height, double scale, MapOrigin origin) =>
        new MapTransform(scale, scale, 0, 0).Then(Canvas(canvasWidth, canvasHeight, width, height, origin));

    /// <summary>
    /// A viewport held inside the picture. The zoom is held to at least 1, because a zoom a hair under
    /// it would leave no room between the edges to hold the position in. How deep it may go is held by
    /// the caller, against the ceiling it was given.
    /// </summary>
    private static MapViewport Within(double zoom, double left, double top)
    {
        zoom = Math.Max(zoom, 1);

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
