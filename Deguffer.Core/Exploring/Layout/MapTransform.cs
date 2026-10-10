namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// A scale on each axis followed by a move: the point (x, y) goes to
/// (x × <paramref name="ScaleX"/> + <paramref name="X"/>, y × <paramref name="ScaleY"/> + <paramref name="Y"/>).
///
/// <para>The one kind of transform the map's picture is put on screen with. Each drawing is placed in
/// the whole picture by one of these (<see cref="MapViewport.Canvas"/>), and the whole picture on the
/// screen by another (<see cref="MapViewport.Camera"/>), so what the screen shows is the two
/// <see cref="Then">composed</see>, and that has to agree with <see cref="MapPlacement"/>, which is
/// what a click is resolved through (§7.1).</para>
/// </summary>
public readonly record struct MapTransform(double ScaleX, double ScaleY, double X, double Y)
{
    /// <summary>Where the point (<paramref name="x"/>, <paramref name="y"/>) goes.</summary>
    public (double X, double Y) Apply(double x, double y) => ((x * ScaleX) + X, (y * ScaleY) + Y);

    /// <summary>
    /// This transform followed by <paramref name="outer"/>, which is what a visual placed by this one
    /// inside a visual placed by <paramref name="outer"/> is placed by.
    /// </summary>
    public MapTransform Then(MapTransform outer) => new(
        ScaleX * outer.ScaleX,
        ScaleY * outer.ScaleY,
        (X * outer.ScaleX) + outer.X,
        (Y * outer.ScaleY) + outer.Y);
}
