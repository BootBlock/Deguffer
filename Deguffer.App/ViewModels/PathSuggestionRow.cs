using Deguffer.Core.Exploring.Network;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One suggestion as the Path… box draws it: the path, where it came from, and an icon for its kind.
/// A value, so the list replaces a row only where what it shows has changed.
/// </summary>
public sealed record PathSuggestionRow(NetworkSuggestion Suggestion)
{
    /// <summary>The path a choice fills the box with, and what the row shows first.</summary>
    public string Path => Suggestion.Place.Path;

    /// <summary>Where it came from, and the letter a mapped share stands behind. Empty for a listed share or folder.</summary>
    public string Detail => NetworkText.Detail(Suggestion);

    public bool HasDetail => Detail.Length > 0;

    public string Glyph => NetworkGlyph.Of(Suggestion.Place.Kind);

    /// <summary>What a screen reader states: the kind and the path, then where it came from.</summary>
    public string Spoken => HasDetail ? $"{NetworkText.Spoken(Suggestion.Place)}. {Detail}" : NetworkText.Spoken(Suggestion.Place);

    /// <summary>
    /// What the box shows once the row is highlighted with the arrow keys, which AutoSuggestBox
    /// reads from here: the path, so Enter uses what is highlighted.
    /// </summary>
    public override string ToString() => Path;
}
