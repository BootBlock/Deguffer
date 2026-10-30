using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>One band of a map's legend: what a colour means, and the colour.</summary>
public readonly record struct LegendBand(string Label, TileColour Colour);

/// <summary>
/// What each colour on a map means, for each way of colouring it, or nothing where the colours are
/// branches.
///
/// <para>A hue per branch explains itself, because the branch it names is the rectangle it is inside.
/// An age, a growth, a kind of file or a depth means nothing without the scale beside it. This is the
/// one place that says which colourings have a scale, so whether a legend is shown and what it lists
/// cannot come to disagree.</para>
/// </summary>
public static class MapLegend
{
    /// <summary>
    /// Every scheme's age bands, built once for the life of the app: the scale does not depend on
    /// what was scanned (G5).
    /// </summary>
    private static readonly IReadOnlyList<LegendBand>[] Ages =
    [
        .. Enum.GetValues<ExploreScheme>().Select(scheme => (IReadOnlyList<LegendBand>)
        [
            .. AgePalette.Bands(scheme).Select(band => new LegendBand(band.Label, band.Colour)),
        ]),
    ];

    /// <summary>The growth scale's bands, which are the same in every scheme (see <see cref="GrowthPalette"/>).</summary>
    private static readonly IReadOnlyList<LegendBand> Growth =
        [.. GrowthPalette.Bands.Select(band => new LegendBand(band.Label, band.Colour))];

    /// <summary>The kinds of file, which are the same in every scheme (see <see cref="TypePalette"/>).</summary>
    private static readonly IReadOnlyList<LegendBand> Types =
        [.. TypePalette.Bands.Select(band => new LegendBand(band.Label, band.Colour))];

    /// <summary>The levels of depth, which are the same in every scheme (see <see cref="DepthPalette"/>).</summary>
    private static readonly IReadOnlyList<LegendBand> Depths =
        [.. DepthPalette.Bands.Select(band => new LegendBand(band.Label, band.Colour))];

    /// <summary>
    /// The bands a map coloured by <paramref name="colouring"/> in <paramref name="scheme"/> needs
    /// explained, and none for branches.
    /// </summary>
    public static IReadOnlyList<LegendBand> For(ExploreColouring colouring, ExploreScheme scheme) => colouring switch
    {
        ExploreColouring.Age => Ages[(int)scheme],
        ExploreColouring.Growth => Growth,
        ExploreColouring.Type => Types,
        ExploreColouring.Depth => Depths,
        _ => [],
    };
}
