namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// A soft light over the point of a canvas the pointer is on, which the GPU adds to the cushions'
/// own (see <see cref="CushionQuads"/>). It brightens each cushion where it faces the light, so the
/// shape under the pointer shows by its slopes, without an outline.
///
/// <para>A point above the canvas rather than a spot on it. A cushion facing the pointer catches
/// more of it than one facing away, which is what makes the map read as something with a surface.
/// It fades to nothing at <see cref="Reach"/>, so everything past that is the picture as shaded
/// without it, pixel for pixel.</para>
/// </summary>
/// <param name="X">The canvas column under the light.</param>
/// <param name="Y">The canvas row under the light.</param>
/// <param name="Reach">How far from that point the light still adds anything, in canvas pixels.</param>
/// <param name="Strength">
/// How much the light adds, at most, to a surface facing it straight on, as a fraction of the colour:
/// the reader's choice (<see cref="Configuration.MapPointerLight.Amount"/>).
/// </param>
public readonly record struct PointerLight(float X, float Y, float Reach, float Strength = PointerLight.StandardStrength)
{
    /// <summary>How far the light reaches, in device-independent pixels, so it covers a few shapes of a crowded picture.</summary>
    public const float ReachInDips = 160;

    /// <summary>
    /// The strength the light has always had: enough to see, and short of washing a pale colour out
    /// to white.
    /// </summary>
    public const float StandardStrength = 0.35f;

    /// <summary>
    /// How high above the canvas the light is, as a fraction of its reach. Low enough that a cushion's
    /// slopes catch it differently, high enough that the light falls on the point under it too.
    /// </summary>
    public const float Height = 0.5f;

    /// <summary>
    /// The light over canvas point <paramref name="x"/>, <paramref name="y"/>, on a display at
    /// <paramref name="scale"/>, adding at most <paramref name="strength"/>.
    /// </summary>
    public static PointerLight At(float x, float y, double scale, float strength) =>
        new(x, y, (float)(ReachInDips * scale), strength);

    /// <summary>
    /// The part of a canvas <paramref name="width"/> by <paramref name="height"/> the light adds to,
    /// in whole pixels, or null where it adds to none of it.
    /// </summary>
    public CanvasRegion? Lit(int width, int height) =>
        Clipped(
            (int)MathF.Floor(X - Reach),
            (int)MathF.Floor(Y - Reach),
            (int)MathF.Ceiling(X + Reach) + 1,
            (int)MathF.Ceiling(Y + Reach) + 1,
            width,
            height);

    /// <summary>
    /// The part of a canvas <paramref name="width"/> by <paramref name="height"/> to shade again for a
    /// light that moves from <paramref name="before"/> to <paramref name="after"/>, either of which may be
    /// none: what the old light lit, which goes back to the picture without it, and what the new one
    /// lights. Null where neither lights anything.
    ///
    /// <para>One rectangle round both, so a light that moves a little, as it does at every pointer
    /// move, is one pass over little more than its own reach.</para>
    /// </summary>
    public static CanvasRegion? Between(PointerLight? before, PointerLight? after, int width, int height)
    {
        var from = before?.Lit(width, height);
        var to = after?.Lit(width, height);

        if (from is not { } a)
        {
            return to;
        }

        if (to is not { } b)
        {
            return a;
        }

        return Clipped(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Bottom, b.Bottom),
            width,
            height);
    }

    private static CanvasRegion? Clipped(int left, int top, int right, int bottom, int width, int height)
    {
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(width, right);
        bottom = Math.Min(height, bottom);

        return right > left && bottom > top ? new CanvasRegion(left, top, right - left, bottom - top) : null;
    }
}
