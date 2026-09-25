namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// A rectangle measured in fractions of whatever it is a part of: the screen, a drawing, or the whole
/// picture, as the member handing it over says.
///
/// <para>In fractions for the reason <see cref="MapViewport"/> is: a map that is resized mid-move goes
/// on meaning the same part of what it shows.</para>
/// </summary>
public readonly record struct MapFrame(double X, double Y, double Width, double Height)
{
    /// <summary>All of it.</summary>
    public static MapFrame Whole => new(0, 0, 1, 1);

    public (double X, double Y) Centre => (X + (Width / 2), Y + (Height / 2));

    /// <summary>
    /// The frame a fraction <paramref name="progress"/> of the way from <paramref name="from"/> to
    /// <paramref name="to"/>, each edge moving in a straight line.
    /// </summary>
    public static MapFrame Between(MapFrame from, MapFrame to, double progress) => new(
        from.X + ((to.X - from.X) * progress),
        from.Y + ((to.Y - from.Y) * progress),
        from.Width + ((to.Width - from.Width) * progress),
        from.Height + ((to.Height - from.Height) * progress));

    /// <summary>The part of this inside <paramref name="bounds"/>, which is empty where none of it is.</summary>
    public MapFrame Within(MapFrame bounds)
    {
        var left = Math.Max(X, bounds.X);
        var top = Math.Max(Y, bounds.Y);
        var right = Math.Min(X + Width, bounds.X + bounds.Width);
        var bottom = Math.Min(Y + Height, bounds.Y + bounds.Height);

        return new MapFrame(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// Where all of what this is a part of goes when this part is stretched onto
    /// <paramref name="onto"/>: the one move of the whole that carries this frame exactly there.
    /// </summary>
    public MapFrame Carried(MapFrame onto)
    {
        var scaleX = onto.Width / Width;
        var scaleY = onto.Height / Height;

        return new MapFrame(onto.X - (X * scaleX), onto.Y - (Y * scaleY), scaleX, scaleY);
    }
}
