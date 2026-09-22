namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Colours stated in OKLCH: lightness, chroma and hue in Björn Ottosson's Oklab space (2020), the
/// space CSS Color 4 adopted for the same reason it is used here.
///
/// <para>Perceptually even, which is the whole point. Two colours at the same lightness here look
/// equally light whatever their hues, so a palette built by turning the hue and stepping the
/// lightness keeps every step visible. The same steps taken in RGB or HSL make yellow glare and
/// blue sink, and the depth a lightness step is meant to say is lost among the hues.</para>
/// </summary>
internal static class Oklch
{
    /// <summary>
    /// How many halvings the chroma search makes. The chroma range here is under 0.4, so twelve
    /// halvings land within a ten-thousandth of the edge of the gamut, which is below a step of an
    /// eight-bit channel.
    /// </summary>
    private const int GamutSteps = 12;

    /// <summary>
    /// The sRGB colour at <paramref name="lightness"/> (0 to 1), <paramref name="chroma"/> and
    /// <paramref name="hue"/> in degrees.
    ///
    /// <para>A colour sRGB cannot show keeps its lightness and hue and loses chroma until it can,
    /// which is the gamut mapping CSS Color 4 specifies in outline. Clipping each channel instead
    /// would shift the hue towards the nearest primary, so two branches a few degrees apart could
    /// come out the same.</para>
    /// </summary>
    public static TileColour ToSrgb(double lightness, double chroma, double hue)
    {
        var radians = hue * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));

        if (Linear(lightness, chroma * cos, chroma * sin) is { } fits)
        {
            return Encode(fits);
        }

        double inside = 0;
        var outside = chroma;

        for (var step = 0; step < GamutSteps; step++)
        {
            var middle = (inside + outside) / 2;

            if (Linear(lightness, middle * cos, middle * sin) is not null)
            {
                inside = middle;
            }
            else
            {
                outside = middle;
            }
        }

        // Chroma zero is inside for any lightness from 0 to 1, which is every lightness the palette
        // asks for, so the search always ends on a colour it checked.
        return Encode(Linear(lightness, inside * cos, inside * sin)
            ?? throw new ArgumentOutOfRangeException(nameof(lightness), lightness, "Outside 0 to 1."));
    }

    /// <summary>
    /// Linear-light sRGB for an Oklab colour, or null where it is outside the gamut. The matrices
    /// are Ottosson's, as published with the space.
    /// </summary>
    private static (double R, double G, double B)? Linear(double l, double a, double b)
    {
        var lp = l + (0.3963377774 * a) + (0.2158037573 * b);
        var mp = l - (0.1055613458 * a) - (0.0638541728 * b);
        var sp = l - (0.0894841775 * a) - (1.2914855480 * b);

        var (lc, mc, sc) = (lp * lp * lp, mp * mp * mp, sp * sp * sp);

        var red = (4.0767416621 * lc) - (3.3077115913 * mc) + (0.2309699292 * sc);
        var green = (-1.2684380046 * lc) + (2.6097574011 * mc) - (0.3413193965 * sc);
        var blue = (-0.0041960863 * lc) - (0.7034186147 * mc) + (1.7076147010 * sc);

        // A hair of tolerance, because a grey computed through three matrices lands a rounding
        // error either side of the gamut's edge and must not be treated as outside it.
        const double Tolerance = 1e-6;

        return red is < -Tolerance or > 1 + Tolerance
            || green is < -Tolerance or > 1 + Tolerance
            || blue is < -Tolerance or > 1 + Tolerance
            ? null
            : (red, green, blue);
    }

    private static TileColour Encode((double R, double G, double B) linear) =>
        new(Channel(linear.R), Channel(linear.G), Channel(linear.B));

    /// <summary>The sRGB transfer function, then rounded to a byte.</summary>
    private static byte Channel(double linear)
    {
        var clamped = Math.Clamp(linear, 0, 1);
        var encoded = clamped <= 0.0031308 ? 12.92 * clamped : (1.055 * Math.Pow(clamped, 1 / 2.4)) - 0.055;

        return (byte)Math.Round(encoded * 255);
    }
}
