using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One preset as the window offers it: its name, what it looks like in a sentence, and a short line
/// of what it sets, so presets can be told apart at a glance before one is tried on the map.
///
/// <para>Which preset an option is, is its place in <see cref="All"/>, in <see cref="MapPreset.All"/>'s
/// order: the list is bound by index, as the window's other lists are.</para>
/// </summary>
/// <param name="Preset">The preset this option applies.</param>
/// <param name="Traits">What it sets, as a few words each: the shading, the lines, the spacing and the colouring.</param>
public sealed record MapPresetOption(MapPreset Preset, string Traits)
{
    /// <summary>Every preset, in the order the window lists them, built once for the life of the app (G5).</summary>
    public static IReadOnlyList<MapPresetOption> All { get; } =
        [.. MapPreset.All.Select(preset => new MapPresetOption(preset, TraitsOf(preset)))];

    public string Name => Preset.Name;

    public string Description => Preset.Description;

    /// <summary>What a screen reader announces for the card, which says in words what its look is.</summary>
    public string Spoken => $"{Name}. {Description}";

    private static string TraitsOf(MapPreset preset)
    {
        var style = preset.Style;

        var shading = style.Shading switch
        {
            MapShading.Bevel => "Bevelled",
            MapShading.Flat => "Flat",
            _ => "Cushioned",
        };

        var lines = (style.BlockBorders, style.FolderBorders) switch
        {
            (MapBorder.None, MapBorder.None) => "no lines",
            (MapBorder.None, _) => "lines round folders",
            _ => "lines round every shape",
        };

        var spacing = preset.Spacing switch
        {
            ExploreSpacing.Dense => "dense",
            ExploreSpacing.Spacious => "spacious",
            _ => "comfortable",
        };

        var traits = $"{shading} · {lines} · {spacing}";

        return preset.Colouring is { } colouring
            ? $"{traits} · {MapAppearanceViewModel.ColouredBy(colouring)}"
            : traits;
    }
}
