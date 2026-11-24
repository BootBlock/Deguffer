using Deguffer.Core.Configuration;

namespace Deguffer.App.Views;

/// <summary>
/// How the treemap spacing setting is presented, wherever it is offered. The Settings page and the map
/// appearance window both write the one stored spacing, and a reader who changes it in one place has
/// to recognise it in the other, so both rows take their icon, words and choices from here rather than
/// each spelling them out.
/// </summary>
public static class TreemapSpacingSetting
{
    public static string Glyph => "\uE80A"; // ViewAll: tiles with room between them

    public static string Title => "Treemap spacing";

    /// <summary>
    /// Bound rather than written in the markup, so <c>DesignSettingsTextTests</c> cannot read it, and
    /// the App's tests hold it to the same one-line budget.
    /// </summary>
    public static string Description =>
        "Room a treemap leaves round each folder and its name. More makes folders easier to tell apart and leaves less for their contents.";

    /// <summary>The choices a box offers, ordered to match <see cref="ExploreSpacing"/> so its index is the stored value.</summary>
    public static IReadOnlyList<string> Choices { get; } = ["Dense", "Comfortable", "Spacious"];
}
