namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// The point of the picture a map's camera is measured from: <paramref name="Left"/> and
/// <paramref name="Top"/>, as fractions of the whole picture, are where the tracker's position and
/// every drawing's placement count from (see <see cref="MapTracking"/> and
/// <see cref="MapViewport.Canvas"/>).
///
/// <para>The compositor keeps the tracker's position, the camera and each placement in single
/// precision, and the screen is the camera's offset plus the zoom times a placement: two numbers that
/// cancel. Measured from the picture's corner, each is about as large as the magnified picture, and
/// zoomed far in a single-precision step of one is a visible fraction of a pixel, which differs from
/// drawing to drawing. Measured from a point near what is shown, they stay small at any zoom.</para>
///
/// <para>The default is the picture's own corner, where every number is as it would be with no
/// origin at all. The camera moves its origin to what it shows when it comes to rest far enough from
/// it (<see cref="Drifted"/>).</para>
/// </summary>
public readonly record struct MapOrigin(double Left, double Top)
{
    /// <summary>
    /// How far from its origin, in pixels, the screen can be before the camera moves the origin. A
    /// single-precision step at this size is under a two-hundredth of a pixel.
    /// </summary>
    public const double Reach = 32768;

    /// <summary>The screen's top-left corner in <paramref name="viewport"/>, as an origin.</summary>
    public static MapOrigin At(MapViewport viewport) => new(viewport.Left, viewport.Top);

    /// <summary>
    /// Whether a screen <paramref name="width"/> by <paramref name="height"/> showing
    /// <paramref name="viewport"/> is more than <see cref="Reach"/> pixels from this origin, measured
    /// in the magnified picture, which is how far the tracker's position is from nothing.
    /// </summary>
    public bool Drifted(MapViewport viewport, double width, double height) =>
        Math.Abs((viewport.Left - Left) * viewport.Zoom * width) > Reach
        || Math.Abs((viewport.Top - Top) * viewport.Zoom * height) > Reach;

    /// <summary>
    /// How far the tracker's position can go across a screen <paramref name="width"/> wide at
    /// <paramref name="scale"/>, measured from this origin: from the picture's left edge to where its
    /// right edge meets the screen's.
    /// </summary>
    public (double Least, double Most) Across(double scale, double width) => Range(Left, scale, width);

    /// <summary>How far the tracker's position can go down a screen <paramref name="height"/> high at <paramref name="scale"/>. See <see cref="Across"/>.</summary>
    public (double Least, double Most) Down(double scale, double height) => Range(Top, scale, height);

    /// <summary>
    /// The edges from an origin <paramref name="corner"/> of the way across: the picture's near edge
    /// is <paramref name="corner"/> of the magnified picture behind it, and its far edge is the rest of
    /// the picture less a screen ahead. The far edge takes what is left of the picture first, in double
    /// precision, because near the far edge that is small and the compositor multiplies it by the
    /// scale in single precision (see the camera's expressions). At a scale of one the two edges are
    /// the same position, and worked out apart they can round a hair the wrong way round.
    /// </summary>
    private static (double Least, double Most) Range(double corner, double scale, double size)
    {
        var least = -corner * scale * size;

        return (least, Math.Max(least, (((1 - corner) * scale) - 1) * size));
    }
}
