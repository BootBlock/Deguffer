using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// How a map's colours are moved from its palette's: made stronger or weaker, then lighter or darker
/// (<see cref="MapStyle.Saturation"/> and <see cref="MapStyle.Brightness"/>).
///
/// <para>Applied to the colour a shape is given, before anything is drawn, so every route to the
/// screen, the text's contrast and the legend all start from the same tuned colour, and no rasteriser
/// or shader knows of it.</para>
///
/// <para>The saturation is the Filter Effects specification's <c>saturate()</c>: each channel moved
/// towards or away from the colour's luminance by the same factor, so a colour keeps its lightness as
/// it loses its hue. The brightness is a mix towards black or white, as <see cref="ShapeFinish.Mix"/>
/// mixes a line. Both in whole numbers out of <see cref="ShapeFinish.Whole"/>, because a drawing asks
/// it once for each of tens of thousands of shapes.</para>
/// </summary>
/// <param name="Saturation">How much of each channel's distance from the luminance is kept, out of <see cref="ShapeFinish.Whole"/>.</param>
/// <param name="Brightness">How far each channel is mixed towards white where positive, or black where negative, out of <see cref="ShapeFinish.Whole"/>.</param>
public readonly record struct ColourTuning(int Saturation, int Brightness)
{
    /// <summary>The palette's own colours, untouched.</summary>
    public static readonly ColourTuning None = new(ShapeFinish.Whole, 0);

    private static readonly TileColour Black = new(0, 0, 0);

    private static readonly TileColour White = new(255, 255, 255);

    /// <summary>Whether this leaves every colour as it is.</summary>
    public bool IsNone => this == None;

    /// <summary>The tuning <paramref name="style"/> asks for.</summary>
    public static ColourTuning Of(MapStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        var saturation = style.Saturation switch
        {
            MapSaturation.Grey => 0,
            MapSaturation.Muted => 115,
            MapSaturation.Soft => 192,
            MapSaturation.Vivid => 341,
            _ => ShapeFinish.Whole,
        };

        // A fifth and two fifths of the way, to the nearest whole part.
        var brightness = style.Brightness switch
        {
            MapBrightness.MuchDarker => -102,
            MapBrightness.Darker => -51,
            MapBrightness.Lighter => 51,
            MapBrightness.MuchLighter => 102,
            _ => 0,
        };

        return new ColourTuning(saturation, brightness);
    }

    /// <summary><paramref name="colour"/> as this tuning moves it.</summary>
    public TileColour Apply(TileColour colour)
    {
        if (IsNone)
        {
            return colour;
        }

        var saturated = Saturation == ShapeFinish.Whole ? colour : Saturate(colour);

        return Brightness switch
        {
            > 0 => ShapeFinish.Mix(saturated, White, Brightness),
            < 0 => ShapeFinish.Mix(saturated, Black, -Brightness),
            _ => saturated,
        };
    }

    /// <summary>
    /// Each channel moved from the colour's luminance by <see cref="Saturation"/>, cut to a byte where a
    /// stronger colour than the screen can show is asked for. The weights are the specification's
    /// 0.213, 0.715 and 0.072, in 256ths that add up to the whole.
    /// </summary>
    private TileColour Saturate(TileColour colour)
    {
        var luminance = ((55 * colour.Red) + (183 * colour.Green) + (18 * colour.Blue)) / ShapeFinish.Whole;

        return new TileColour(
            Channel(colour.Red, luminance),
            Channel(colour.Green, luminance),
            Channel(colour.Blue, luminance));
    }

    private byte Channel(byte channel, int luminance) =>
        (byte)Math.Clamp(luminance + ((channel - luminance) * Saturation / ShapeFinish.Whole), 0, 255);
}
