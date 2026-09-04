namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The shading model both rasterisers draw with: van Wijk and van de Wetering,
/// <i>Cushion Treemaps</i>, Proc. IEEE InfoVis '99, pp. 73–78, at the values WinDirStat and
/// KDirStat both settled on.
///
/// <para>The shading is what makes nesting legible without a border around every rectangle, and
/// this is the single-ridge form: each shape is shaded from its own bounds with the ridge height
/// scaled down by depth, rather than accumulating its ancestors' surfaces into it. The difference
/// is visible only where a deep shape sits on a steep part of its parent's cushion, and the
/// accumulating form needs the whole ancestor chain carried through the layout.</para>
///
/// <para>Stated once, here, rather than in each rasteriser. The rectangles and the sectors differ
/// only in how a pixel's position within a shape becomes a surface normal; the light, the
/// reflection model and the depth falloff are the same picture and have to stay the same picture,
/// or a drive drawn one way and then the other would appear to be lit twice.</para>
/// </summary>
public static class CushionShading
{
    private const double RidgeHeight = 0.38;
    private const double DepthScale = 0.91;
    private const double Ambient = 0.13;
    private const double Diffuse = 0.87;

    // Van Wijk's light, l = [1, 2, 10] normalised, with the two lateral components negated because
    // a bitmap's y axis runs downwards and the paper's does not. Negating both puts the key light
    // above and to the left, which is where every implementation of this puts it and where a reader
    // expects a highlight to be.
    //
    // The model is ambient plus Lambertian diffuse, and nothing else. There is no specular term:
    // the paper is explicit that "a simple model, i.e. diffuse reflection, suffices", and a
    // highlight would read as a material rather than as a shape.
    private const double LightX = -0.09759;
    private const double LightY = -0.19518;
    private const double LightZ = 0.9759;

    /// <summary>
    /// The ridge height at every depth a real tree reaches, worked out once.
    ///
    /// <para><see cref="RidgeAt"/> is asked per pixel of a sunburst and per rectangle per band of a
    /// treemap, which is millions of times a repaint, and <see cref="Math.Pow(double, double)"/> is
    /// nowhere near cheap enough to be asked that often (G4/G5). Sixty-four levels is past the point
    /// where the ridge is flat to within a byte of a colour channel, and a deeper shape than that
    /// falls back to the arithmetic rather than to a wrong answer.</para>
    /// </summary>
    private static readonly double[] Ridges = Enumerable
        .Range(0, 64)
        .Select(depth => 4 * RidgeHeight * Math.Pow(DepthScale, depth))
        .ToArray();

    /// <summary>
    /// How far the surface tilts at the edges of a shape at this depth. The ridge flattens as it
    /// goes down, so a nested shape reads as sitting on its parent rather than competing with it.
    /// </summary>
    public static double RidgeAt(int depth) => (uint)depth < (uint)Ridges.Length
        ? Ridges[depth]
        : 4 * RidgeHeight * Math.Pow(DepthScale, depth);

    /// <summary>
    /// How brightly a point whose surface normal tilts by <paramref name="nx"/> and
    /// <paramref name="ny"/> is lit, from <see cref="Ambient"/> at the darkest to one.
    /// </summary>
    public static double LightAt(double nx, double ny)
    {
        var cosine = ((nx * LightX) + (ny * LightY) + LightZ)
            / Math.Sqrt((nx * nx) + (ny * ny) + 1);

        return Ambient + (Diffuse * Math.Clamp(cosine, 0, 1));
    }

    /// <summary>Write one lit pixel, as the four bytes a BGRA buffer wants them in.</summary>
    public static void Write(byte[] pixels, int offset, TileColour colour, double light)
    {
        pixels[offset] = Shade(colour.Blue, light);
        pixels[offset + 1] = Shade(colour.Green, light);
        pixels[offset + 2] = Shade(colour.Red, light);
        pixels[offset + 3] = 255;
    }

    private static byte Shade(byte channel, double light) => (byte)Math.Clamp(channel * light, 0, 255);
}
