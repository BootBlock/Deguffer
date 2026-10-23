namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The sheen a <see cref="Configuration.MapShading.Gradient"/> shape is drawn with: its own colour
/// across, brightened towards its lit edge and dimmed towards the far one in a straight line, so the
/// middle is the colour itself. A treemap's lit edge is its top; a sunburst's is a ring's inner edge.
///
/// <para>Whole numbers out of <see cref="ShapeFinish.Whole"/>, as a mix is, and reproduced in
/// <c>Cushions.hlsl</c>, so the shader draws the same picture exactly rather than to within a rounding
/// of it. A float against a double would differ in the last bit of a whole row at once, because the
/// light is the same all the way along each row.</para>
/// </summary>
public static class GradientShading
{
    /// <summary>
    /// How much lighter the lit edge is than the shape's colour at the standard relief, and the far edge
    /// darker: enough to read as a surface lit from above, short of a pale colour reaching white.
    /// </summary>
    public const float Sheen = 0.16f;

    /// <summary>
    /// How far the light falls from the lit edge to the middle at <paramref name="relief"/>, in parts of
    /// <see cref="ShapeFinish.Whole"/>.
    /// </summary>
    public static int SlopeAt(float relief) => (int)MathF.Round(relief * Sheen * ShapeFinish.Whole);

    /// <summary>
    /// How brightly a point <paramref name="along"/> of <paramref name="length"/> from the lit edge is
    /// lit, in parts of <see cref="ShapeFinish.Whole"/>: the whole and <paramref name="slope"/> more at
    /// the lit edge, the whole in the middle, and <paramref name="slope"/> less at the far edge. A shape
    /// with no length between its edges is lit as its middle.
    /// </summary>
    /// <param name="along">How far from the lit edge, from zero to <paramref name="length"/>.</param>
    public static int LightAt(int slope, int along, int length) => length <= 0
        ? ShapeFinish.Whole
        : ShapeFinish.Whole - slope + (2 * slope * (length - along) / length);

    /// <summary><paramref name="colour"/> lit by <paramref name="light"/> parts of <see cref="ShapeFinish.Whole"/>, each channel cut to a whole level.</summary>
    public static TileColour Lit(TileColour colour, int light) => new(
        Lit(colour.Red, light),
        Lit(colour.Green, light),
        Lit(colour.Blue, light));

    private static byte Lit(byte channel, int light) => (byte)Math.Min(255, (channel * light) >> 8);
}
