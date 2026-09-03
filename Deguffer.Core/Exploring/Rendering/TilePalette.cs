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

    /// <summary>This colour scaled towards black or white, for the depth shading.</summary>
    public TileColour Scaled(double factor) => new(
        Clamp(Red * factor), Clamp(Green * factor), Clamp(Blue * factor));

    private static byte Clamp(double value) => (byte)Math.Clamp(value, 0, 255);

    private static double Linear(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// What colour each rectangle is drawn in.
///
/// <para>Hue identifies the top-level branch a rectangle belongs to, and lightness says how deep it
/// sits. That pairing is the one of the four schemes in common use that survives a colour-vision
/// deficiency: the alternatives colour by extension, by depth alone, or by a single ramp, and the
/// first of those is where the reference implementation went wrong — WinDirStat's default extension
/// palette puts pure red at index 1 and pure green at index 2, which is the exact pair deuteranopia
/// and protanopia cannot separate.</para>
///
/// <para>The seven hues are Okabe and Ito's Color Universal Design set, chosen because it is the
/// most widely used categorical palette that is distinguishable under all three common
/// deficiencies. WinDirStat's own newer views moved to it too. The eighth entry is a neutral grey
/// rather than that palette's black, which would read as a hole in the picture rather than as a
/// branch.</para>
/// </summary>
public static class TilePalette
{
    private static readonly TileColour[] Branches =
    [
        TileColour.FromRgb(0xE69F00), // orange
        TileColour.FromRgb(0x56B4E9), // sky blue
        TileColour.FromRgb(0x009E73), // bluish green
        TileColour.FromRgb(0xF0E442), // yellow
        TileColour.FromRgb(0x0072B2), // blue
        TileColour.FromRgb(0xD55E00), // vermillion
        TileColour.FromRgb(0xCC79A7), // reddish purple
        TileColour.FromRgb(0x999999), // neutral, for the eighth branch; a ninth wraps to the first
    ];

    /// <summary>
    /// The colour for a rectangle in <paramref name="branch"/> at <paramref name="depth"/>.
    ///
    /// <para>The lightness cycle is deliberately short. Four steps distinguish a parent from its
    /// child without the eighth level being white, and a longer ramp would make two distant depths
    /// of the same branch look like different branches.</para>
    /// </summary>
    public static TileColour For(int branch, int depth) =>
        Branches[Math.Abs(branch) % Branches.Length].Scaled(0.86 + (Math.Abs(depth) % 4 * 0.07));

    /// <summary>
    /// The colour for a rectangle standing in for siblings too small to draw.
    ///
    /// <para>Deliberately outside the branch hues, and deliberately flat. It is not a thing on the
    /// disk, so giving it a colour that reads as one would invite the user to act on it.</para>
    /// </summary>
    public static TileColour Aggregate => TileColour.FromRgb(0x707070);
}
