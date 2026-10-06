using Deguffer.Core.Exploring.History;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>One step of the growth scale: what it means, and what it is drawn in.</summary>
/// <param name="Label">What the legend says beside the swatch.</param>
public readonly record struct GrowthBand(string Label, TileColour Colour);

/// <summary>
/// What colour a shape is painted when the map is coloured by how much it grew since an earlier scan.
///
/// <para><b>Diverging, from purple through grey to orange.</b> Growth has a sign and a size, so the
/// scale needs a neutral middle and two directions a reader can tell apart without the legend. Purple
/// and orange stay apart under all three common colour-vision deficiencies, where red and green do
/// not, and each side darkens as the change grows, so the order reads in grey as well. The hues are
/// ColorBrewer's PuOr, darkened at the two ends. Each step was checked against the next for
/// separation under normal vision and under each deficiency, and the middle grey is light enough to
/// stand apart from the grey for "not compared" and dark enough not to vanish into the map's
/// ground.</para>
///
/// <para><b>Orange for growth.</b> Growth is what the user came to find, so it takes the warmer and
/// louder side.</para>
///
/// <para><b>Bands in bytes, not in proportion.</b> The question is "what filled my drive", which is
/// answered in bytes: a folder that doubled from 2 MB to 4 MB matters less than one that went from
/// 200 GB to 210 GB.</para>
///
/// <para><b>One scale for every <see cref="Configuration.ExploreScheme"/>.</b> The meaning is in the
/// two hues and the neutral middle, and a set that moved them would move the meaning.</para>
/// </summary>
public static class GrowthPalette
{
    private const long Megabyte = 1024L * 1024;
    private const long Gigabyte = 1024L * Megabyte;

    /// <summary>
    /// Every band, the largest growth first, then the unchanged, then the largest shrinkage, then the
    /// band for a shape nothing could compare.
    /// </summary>
    private static readonly GrowthBand[] Scale =
    [
        new("Grew 1 GB or more", TileColour.FromRgb(0x8C2D04)),
        new("Grew 100 MB to 1 GB", TileColour.FromRgb(0xE66101)),
        new("Grew under 100 MB", TileColour.FromRgb(0xFDB863)),
        new("No change", TileColour.FromRgb(0xE0E0E0)),
        new("Shrank under 100 MB", TileColour.FromRgb(0xB2ABD2)),
        new("Shrank 100 MB to 1 GB", TileColour.FromRgb(0x8073AC)),
        new("Shrank 1 GB or more", TileColour.FromRgb(0x3F007D)),
        NotCompared,
    ];

    /// <summary>
    /// What a shape is painted where nothing up to the root could be compared: a scan with no earlier
    /// one, or a drawing of another tree. The grey the age scale gives an undated entry, and for the
    /// same reason: it is off the scale, so it must not read as a step of it.
    /// </summary>
    private static GrowthBand NotCompared => new("Not compared", TileColour.FromRgb(0x9E9E9E));

    /// <summary>Every band, in the order a legend lists them.</summary>
    public static IReadOnlyList<GrowthBand> Bands => Scale;

    /// <summary>The band a change of <paramref name="change"/> falls in.</summary>
    public static GrowthBand BandOf(FolderChange? change)
    {
        if (change is not { } known)
        {
            return Scale[^1];
        }

        var bytes = known.Bytes;
        var size = Math.Abs(bytes);
        var step = size >= Gigabyte ? 0 : size >= 100 * Megabyte ? 1 : 2;

        return bytes switch
        {
            > 0 => Scale[step],
            < 0 => Scale[6 - step],
            _ => Scale[3],
        };
    }

    /// <summary>What to paint a shape whose change is <paramref name="change"/>.</summary>
    public static TileColour For(FolderChange? change) => BandOf(change).Colour;
}
