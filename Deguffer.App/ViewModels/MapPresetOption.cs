using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One preset as the window offers it: its name, and what it looks like in a sentence, so presets can
/// be told apart at a glance before one is tried on the map.
///
/// <para>Which preset an option is, is its place in <see cref="All"/>, in <see cref="MapPreset.All"/>'s
/// order: the list is bound by index, as the window's other lists are.</para>
/// </summary>
/// <param name="Preset">The preset this option applies.</param>
public sealed record MapPresetOption(MapPreset Preset)
{
    /// <summary>Every preset, in the order the window lists them, built once for the life of the app (G5).</summary>
    public static IReadOnlyList<MapPresetOption> All { get; } =
        [.. MapPreset.All.Select(preset => new MapPresetOption(preset))];

    public string Name => Preset.Name;

    public string Description => Preset.Description;

    /// <summary>What a screen reader announces for the row, which says in words what its look is.</summary>
    public string Spoken => $"{Name}. {Description}";
}
