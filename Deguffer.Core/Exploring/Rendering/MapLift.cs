namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Where the shape under the pointer is lifted out of a drawing: the part of the canvas its mask has
/// to cover.
/// </summary>
public static class MapLift
{
    /// <summary>
    /// The pixels of a canvas <paramref name="width"/> by <paramref name="height"/> that
    /// <paramref name="outlines"/> cover, rounded out to whole pixels with one more round them for the
    /// edge a filled shape is smoothed into; or null where they cover none of the canvas.
    ///
    /// <para>Held to the canvas, because nothing of the drawing is lifted past its edge, and the outline
    /// of a folder in a zoomed drawing can run many canvases beyond it: a mask the size of that would
    /// be memory for pixels nothing shows, or more than a surface can hold.</para>
    /// </summary>
    public static CanvasRegion? Area(IReadOnlyList<ExploreOutline> outlines, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(outlines);

        var (left, top, right, bottom) = (float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity);

        foreach (var outline in outlines)
        {
            foreach (var point in outline.Points)
            {
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
                right = Math.Max(right, point.X);
                bottom = Math.Max(bottom, point.Y);
            }
        }

        // Held in floating point before it is made whole, as a point far off the canvas is past what
        // an int holds. No points leave the infinities in place, which come out as an empty area.
        var x = (int)Math.Clamp(MathF.Floor(left) - 1, 0, width);
        var y = (int)Math.Clamp(MathF.Floor(top) - 1, 0, height);
        var r = (int)Math.Clamp(MathF.Ceiling(right) + 1, 0, width);
        var b = (int)Math.Clamp(MathF.Ceiling(bottom) + 1, 0, height);

        return r > x && b > y ? new CanvasRegion(x, y, r - x, b - y) : null;
    }
}
