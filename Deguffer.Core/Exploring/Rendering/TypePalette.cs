using Deguffer.Core.Exploring.Files;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>One entry of the type legend: what it names, and what it is drawn in.</summary>
public readonly record struct TypeBand(string Label, TileColour Colour);

/// <summary>
/// What colour a shape is painted when the map is coloured by the kind of file it holds. See
/// <see cref="DominantTypes"/> for which kind a folder is painted.
///
/// <para><b>Paul Tol's muted set, and not a hue wheel.</b> The kinds are categories with no order, so
/// the colours must be told apart and must not read as a scale. Tol's set of nine was chosen to stay
/// apart under all three common colour-vision deficiencies, and nine is the number of kinds a name can
/// say. <see cref="FileCategory.Other"/> takes the pale grey Tol pairs with the set, so the kinds no
/// name explains recede.</para>
///
/// <para><b>Colour is never the only carrier (§6.5).</b> The legend names every colour, and the line
/// under the map names the kind of whatever the pointer is over.</para>
///
/// <para><b>One set for every <see cref="Configuration.ExploreScheme"/>.</b> The meaning is in which
/// colour goes with which kind, and a set that moved them would move the meaning, as with
/// <see cref="GrowthPalette"/>.</para>
/// </summary>
public static class TypePalette
{
    /// <summary>Indexed by <see cref="FileCategory"/>.</summary>
    private static readonly TileColour[] Colours =
    [
        TileColour.FromRgb(0x882255), // Video: wine
        TileColour.FromRgb(0xAA4499), // Audio: purple
        TileColour.FromRgb(0x117733), // Images: green
        TileColour.FromRgb(0x88CCEE), // Documents: cyan
        TileColour.FromRgb(0xDDCC77), // Archives: sand
        TileColour.FromRgb(0x999933), // Disk images: olive
        TileColour.FromRgb(0x332288), // Virtual machine disks: indigo
        TileColour.FromRgb(0xCC6677), // Installers: rose
        TileColour.FromRgb(0x44AA99), // Code and build output: teal
        TileColour.FromRgb(0xDDDDDD), // Other: pale grey
    ];

    /// <summary>
    /// What a folder is painted before its kind has been measured: the grey the age scale gives an
    /// undated entry, for the same reason. It is off the scale, so it must not read as a kind.
    /// </summary>
    private static readonly TypeBand NotMeasured = new("Not measured yet", TileColour.FromRgb(0x9E9E9E));

    /// <summary>Every kind in the order a picker lists them, then the band for a folder not measured yet.</summary>
    private static readonly TypeBand[] Scale =
        [.. FileCategories.All.Select(category => new TypeBand(FileCategories.Label(category), For(category))), NotMeasured];

    /// <summary>Every band, in the order a legend lists them.</summary>
    public static IReadOnlyList<TypeBand> Bands => Scale;

    /// <summary>What a folder is painted before its kind has been measured.</summary>
    public static TileColour Unmeasured => NotMeasured.Colour;

    /// <summary>What a shape holding mostly <paramref name="category"/> is painted.</summary>
    public static TileColour For(FileCategory category) => Colours[(int)category];
}
