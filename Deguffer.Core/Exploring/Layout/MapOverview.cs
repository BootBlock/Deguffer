namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Which corner of the map its overview stands in: one of the top two, because the page's notes about
/// a scan stand along the bottom.
/// </summary>
public enum MapCorner
{
    TopRight,
    TopLeft,
}

/// <summary>
/// The overview a zoomed map shows in a corner: a small copy of the whole picture, with a rectangle
/// round the part of it on the screen. How large it is, where the rectangle is, which corner it
/// stands in, and where a press on it takes the camera.
///
/// <para>In Core, as <see cref="MapViewport"/> is, so the rules are provable without a window (G8).
/// The compositor works the rectangle out again from the camera at every frame, by the same
/// arithmetic as <see cref="Frame"/>, and a press is resolved through this, so the two must agree.</para>
/// </summary>
public static class MapOverview
{
    /// <summary>The longest the overview's longer side is, in device-independent pixels.</summary>
    public const double Side = 200;

    /// <summary>The most of the map's width, and of its height, the overview takes.</summary>
    public const double Share = 0.25;

    /// <summary>
    /// The shortest the overview's shorter side can be and still be worth showing. A map smaller
    /// than this allows has no overview: it would be too small to aim at, and over too much of the map.
    /// </summary>
    public const double LeastSide = 48;

    /// <summary>How far the overview stands from the map's edges.</summary>
    public const double Margin = 12;

    /// <summary>
    /// How near the overview the map can be acted on before it moves to the other corner: the
    /// shape a wheel turns or a drag holds is the one under the pointer, and the overview never
    /// covers it.
    /// </summary>
    public const double Reach = 24;

    /// <summary>
    /// The least the rectangle round the screen is on a side, in device-independent pixels. Deep in,
    /// the part on the screen is far smaller than a pixel of the overview, and a rectangle of that
    /// size could be neither seen nor pressed.
    /// </summary>
    public const double LeastFrame = 8;

    /// <summary>
    /// The overview's size on a map <paramref name="width"/> by <paramref name="height"/>, or null
    /// where the map is too small for one. The picture is laid out over the whole map, so the overview
    /// has the map's shape, and the rectangle in it has the screen's.
    /// </summary>
    public static (double Width, double Height)? Size(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var scale = Math.Min(Side / Math.Max(width, height), Share);
        var size = (Width: width * scale, Height: height * scale);

        return Math.Min(size.Width, size.Height) < LeastSide ? null : size;
    }

    /// <summary>
    /// The rectangle round the part of the picture <paramref name="shown"/> puts on the screen, in
    /// fractions of an overview <paramref name="width"/> by <paramref name="height"/>: the part itself,
    /// or where that is under <see cref="LeastFrame"/> on a side, a rectangle that size about its
    /// middle. Past the overview's edges only as far as a drag stretches the picture past its own.
    /// </summary>
    public static MapFrame Frame(MapViewport shown, double width, double height)
    {
        var across = Math.Max(1 / shown.Zoom, LeastFrame / width);
        var down = Math.Max(1 / shown.Zoom, LeastFrame / height);
        var centreX = shown.Left + (0.5 / shown.Zoom);
        var centreY = shown.Top + (0.5 / shown.Zoom);

        return new MapFrame(centreX - (across / 2), centreY - (down / 2), across, down);
    }

    /// <summary>
    /// The camera at <paramref name="at"/>'s zoom with picture point (<paramref name="x"/>,
    /// <paramref name="y"/>) in the middle of the screen, as far as the picture's edges allow: where
    /// a click on the overview flies to.
    /// </summary>
    public static MapViewport Centred(MapViewport at, double x, double y, double ceiling) =>
        MapViewport.Anchored(at.Zoom, x, y, 0.5, 0.5, ceiling);

    /// <summary>
    /// The camera <paramref name="from"/> moved by (<paramref name="x"/>, <paramref name="y"/>) of
    /// the overview, which is that much of the whole picture: where a drag of the rectangle takes it.
    /// The rectangle goes where the hand goes, and the screen with it, as far as the picture's edges.
    /// </summary>
    public static MapViewport Dragged(MapViewport from, double x, double y) =>
        from.Panned(-x * from.Zoom, -y * from.Zoom);

    /// <summary>
    /// Where the overview's top-left corner is on a map <paramref name="width"/> by
    /// <paramref name="height"/>, in <paramref name="corner"/>, at <paramref name="size"/>.
    /// </summary>
    public static (double X, double Y) Corner(MapCorner corner, double width, double height, (double Width, double Height) size) =>
        (corner == MapCorner.TopRight ? width - Margin - size.Width : Margin, Margin);

    /// <summary>
    /// The corner the overview stands in once the map is acted on at (<paramref name="x"/>,
    /// <paramref name="y"/>), on a map <paramref name="width"/> by <paramref name="height"/>: the
    /// other one, where the point is within <see cref="Reach"/> of it in <paramref name="corner"/>, and
    /// <paramref name="corner"/> otherwise. A point is never within reach of both: the overview takes
    /// at most <see cref="Share"/> of the map's width, and a map too narrow to part the two corners by
    /// twice the reach is too narrow for an overview.
    /// </summary>
    public static MapCorner Avoiding(MapCorner corner, double x, double y, double width, double height)
    {
        if (Size(width, height) is not { } size)
        {
            return corner;
        }

        var (left, top) = Corner(corner, width, height, size);
        var near = x >= left - Reach && x <= left + size.Width + Reach && y >= top - Reach && y <= top + size.Height + Reach;

        return !near ? corner : corner == MapCorner.TopRight ? MapCorner.TopLeft : MapCorner.TopRight;
    }
}
