using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;
using Windows.UI;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One scheme as the window offers it: a name, what it looks like in a sentence, and a strip of
/// its colours, so the choice can be made by eye before it is made on the map.
///
/// <para>Which scheme an option is, is its place in <see cref="All"/>: the list is bound by index,
/// as the other boxes indexed against an enum are, so <see cref="All"/> is in
/// <see cref="ExploreScheme"/>'s order and built from it.</para>
///
/// <para>The strips are colours rather than brushes. A brush is a XAML object, and one made in a
/// static initialiser ties every reader of <see cref="All"/> to a running XAML host; the window's
/// template paints each swatch from its colour instead. The strips are decoration: the name and the
/// sentence say what the scheme is, with or without them (§6.5).</para>
/// </summary>
/// <param name="Branches">A few branch colours at the first level, as a treemap's largest folders are drawn.</param>
/// <param name="Ages">The age bands, newest first, without the grey for an undated entry.</param>
public sealed record MapSchemeOption(
    string Name,
    string Description,
    IReadOnlyList<MapSwatch> Branches,
    IReadOnlyList<MapSwatch> Ages)
{
    /// <summary>
    /// Hues spread round the circle, and alternately lifted, as a folder's children are given them.
    /// </summary>
    private static readonly BranchHue[] SampleHues =
    [
        new(0, 30, false),
        new(60, 30, true),
        new(120, 30, false),
        new(180, 30, true),
        new(240, 30, false),
        new(300, 30, true),
    ];

    /// <summary>Every scheme, in <see cref="ExploreScheme"/>'s order, built once for the life of the app (G5).</summary>
    public static IReadOnlyList<MapSchemeOption> All { get; } =
        [.. Enum.GetValues<ExploreScheme>().Select(Option)];

    /// <summary>What a screen reader announces for the row, which has no text beside the swatches.</summary>
    public string Spoken => $"{Name}. {Description}";

    private static MapSchemeOption Option(ExploreScheme scheme)
    {
        var (name, description) = scheme switch
        {
            ExploreScheme.Vivid => ("Vivid", "Stronger colours. Ages run from yellow through orange to blue."),
            ExploreScheme.Soft => ("Soft", "Pale colours. Ages run from pale yellow to lilac."),
            ExploreScheme.Deep => ("Deep", "Darker colours. Ages run from cream through red to dark purple."),
            _ => ("Standard", "Even colours. Ages run from yellow to purple."),
        };

        return new(
            name,
            description,
            [.. SampleHues.Select(hue => new MapSwatch(TilePalette.For(hue, 1, scheme)))],
            [.. AgePalette.Bands(scheme).SkipLast(1).Select(band => new MapSwatch(band.Colour))]);
    }
}

/// <summary>One colour of a scheme's strip, as the map draws it.</summary>
/// <param name="Tile">The colour as the map's palette states it.</param>
public sealed record MapSwatch(TileColour Tile)
{
    /// <summary>The colour the window's template paints the swatch in. Opaque, as a tile is drawn.</summary>
    public Color Colour => Color.FromArgb(255, Tile.Red, Tile.Green, Tile.Blue);
}
