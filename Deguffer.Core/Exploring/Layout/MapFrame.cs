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

    /// <summary>The part of this inside <paramref name="bounds"/>, which is empty where none of it is.</summary>
    public MapFrame Clipped(MapFrame bounds)
    {
        var left = Math.Max(X, bounds.X);
        var top = Math.Max(Y, bounds.Y);
        var right = Math.Min(X + Width, bounds.X + bounds.Width);
        var bottom = Math.Min(Y + Height, bounds.Y + bounds.Height);

        return new MapFrame(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// Where <paramref name="part"/>, measured in fractions of this frame, is in the terms this frame
    /// is measured in.
    /// </summary>
    public MapFrame Inside(MapFrame part) =>
        new(X + (part.X * Width), Y + (part.Y * Height), part.Width * Width, part.Height * Height);
}
