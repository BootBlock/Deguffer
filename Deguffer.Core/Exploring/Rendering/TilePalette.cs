using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// A colour, as the four bytes a bitmap wants them in. Kept here rather than taken from the UI
/// framework so the whole of drawing stays testable without a window (G8).
/// </summary>
public readonly record struct TileColour(byte Red, byte Green, byte Blue)
{
    public static TileColour FromRgb(uint value) =>
        new((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    /// <summary>
    /// Relative luminance, per WCAG 2.x: linearise each channel, then weight them
    /// 0.2126 / 0.7152 / 0.0722.
    /// </summary>
    public double RelativeLuminance =>
        (0.2126 * Linear(Red)) + (0.7152 * Linear(Green)) + (0.0722 * Linear(Blue));

    /// <summary>
    /// Black or white, whichever this colour gives more contrast against.
    ///
    /// <para>§6.5 requires the UI to be legible with no backdrop at all, and a treemap puts text
    /// over whatever colour the rectangle underneath happens to be. A fixed label colour is legible
    /// over roughly half a palette; computing it per rectangle is legible over all of it, and it is
    /// the same code in light and dark themes.</para>
    /// </summary>
    public TileColour ContrastingText =>
        (RelativeLuminance + 0.05) / 0.05 >= 1.05 / (RelativeLuminance + 0.05)
            ? new TileColour(0, 0, 0)
            : new TileColour(255, 255, 255);

    /// <summary>
    /// Written out rather than left to the record, whose generated version walks every public
    /// property — <see cref="ContrastingText"/> among them, which is another
    /// <see cref="TileColour"/>. That recurses until the stack runs out, so a failing assertion on
    /// a colour took the test host down instead of reporting which colour it got.
    /// </summary>
    public override string ToString() => $"#{Red:X2}{Green:X2}{Blue:X2}";

    private static double Linear(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// What colour each shape is drawn in when the colours say where it belongs.
///
/// <para>Hue says which folder a shape is part of, and lightness says how deep it sits. The hue
/// comes from <see cref="BranchHues"/>, which gives every folder an arc of the circle inside its
/// parent's, so everything in one top-level folder shares one part of the circle and a folder is
/// told from its neighbour by its own part of that. Both are stated in <see cref="Oklch"/>, whose
/// lightness is the same lightness whatever the hue, so a depth step is visible in every
/// branch.</para>
///
/// <para>Hue is not the only cue, because a hue wheel is exactly what red-green colour blindness
/// takes away, and a small categorical set such as Okabe and Ito's, which survives it, has too few
/// colours to give every folder its own. So neighbouring siblings alternate a step of lightness,
/// deeper shapes are lighter and less saturated, and a folder with room for it is framed and named:
/// each of those says where a shape belongs without hue.</para>
///
/// <para>Each <see cref="ExploreScheme"/> moves where that ramp starts and how saturated it is, and
/// none changes its shape: every one still rises by depth for the same number of levels and lifts
/// alternate siblings, because those steps are what say where a shape belongs.</para>
/// </summary>
public static class TilePalette
{
    /// <summary>
    /// Indexed by <see cref="ExploreScheme"/>. The Oklch gamut mapping takes a saturation the screen
    /// cannot show back to one it can at the same lightness and hue, so a vivid set asks for more
    /// than some hues can give and each hue gets as much of it as it can.
    /// </summary>
    private static readonly Ramp[] Ramps =
    [
        new(RootLightness: 0.62, FirstLightness: 0.64, LightnessStep: 0.055, Lift: 0.045, FirstChroma: 0.14, ChromaStep: 0.015),
        new(RootLightness: 0.58, FirstLightness: 0.60, LightnessStep: 0.06, Lift: 0.05, FirstChroma: 0.20, ChromaStep: 0.02),
        new(RootLightness: 0.74, FirstLightness: 0.78, LightnessStep: 0.03, Lift: 0.035, FirstChroma: 0.075, ChromaStep: 0.008),
        new(RootLightness: 0.48, FirstLightness: 0.50, LightnessStep: 0.06, Lift: 0.05, FirstChroma: 0.13, ChromaStep: 0.012),
    ];

    /// <summary>
    /// How many levels the lightness keeps rising for. Past it every level is drawn alike, because a
    /// longer ramp takes the deepest shapes so close to white that no two hues can be told apart.
    /// </summary>
    private const int Levels = 4;

    /// <summary>
    /// The colour for a shape owning <paramref name="hue"/> at <paramref name="depth"/> below the
    /// drawing's root, in <paramref name="scheme"/>.
    ///
    /// <para>The root itself owns the whole circle, which has no hue to give it, so it is drawn a
    /// neutral grey: it is the frame round everything else.</para>
    /// </summary>
    public static TileColour For(BranchHue hue, int depth, ExploreScheme scheme)
    {
        var ramp = Ramps[(int)scheme];

        if (hue.Sweep >= 360 || depth <= 0)
        {
            return Oklch.ToSrgb(ramp.RootLightness, 0, 0);
        }

        var level = Math.Min(depth - 1, Levels);
        var lightness = ramp.FirstLightness + (level * ramp.LightnessStep) + (hue.Lifted ? ramp.Lift : 0);
        var chroma = ramp.FirstChroma - (level * ramp.ChromaStep);

        return Oklch.ToSrgb(lightness, chroma, hue.Centre);
    }

    /// <summary>
    /// The colour for a rectangle standing in for siblings too small to draw.
    ///
    /// <para>Deliberately outside the branch hues, and deliberately flat. It is not a thing on the
    /// disk, so giving it a colour that reads as one would invite the user to act on it.</para>
    /// </summary>
    public static TileColour Aggregate => TileColour.FromRgb(0x707070);

    /// <summary>
    /// The colour for the block standing for a volume's free space: a light neutral, apart from
    /// the aggregate's darker one and from every hue, because it is neither a thing on the disk nor
    /// a run of them.
    /// </summary>
    public static TileColour FreeSpace => TileColour.FromRgb(0xC8C8C8);

    /// <summary>
    /// The colour for the block standing for use the scan did not account for: the neutral between
    /// the aggregate's and the free space's, because it is in use like the one and not a thing on
    /// the disk like the other.
    /// </summary>
    public static TileColour Unaccounted => TileColour.FromRgb(0x9C9C9C);

    /// <summary>Where one scheme's lightness and saturation start, and how far each level moves them.</summary>
    private readonly record struct Ramp(
        double RootLightness,
        double FirstLightness,
        double LightnessStep,
        double Lift,
        double FirstChroma,
        double ChromaStep);
}
