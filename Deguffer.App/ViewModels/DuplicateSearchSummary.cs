using Deguffer.Core.Configuration;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The search as one line, for the heading of the page's search settings while they are folded away:
/// where it looks, what makes a match, and any filter that narrows it. Words only; what the search
/// is stays <see cref="DuplicatePreferences.Search"/>'s.
/// </summary>
public static class DuplicateSearchSummary
{
    private const string Separator = " · ";

    public static string Of(IReadOnlyList<SearchLocation> locations, DuplicatePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(preferences);

        List<string> parts = [Where(locations)];
        var references = locations.Count(location => location.Role == LocationRole.Reference);

        if (references > 0)
        {
            parts.Add(references == 1 ? "1 reference" : $"{references:N0} references");
        }

        parts.Add(preferences.Criteria.ReadsContent()
            ? $"matched on {preferences.Criteria.Described()} ({preferences.OfferedAlgorithm.Name()})"
            : $"matched on {preferences.Criteria.Described()}");

        if (Sizes(preferences.Sizes) is { } sizes)
        {
            parts.Add(sizes);
        }

        if (Extensions(preferences.ExtensionFilter) is { } extensions)
        {
            parts.Add(extensions);
        }

        return string.Join(Separator, parts);
    }

    private static string Where(IReadOnlyList<SearchLocation> locations) => locations.Count switch
    {
        0 => "No drive or folder chosen",
        1 => locations[0].Path,
        2 => $"{locations[0].Path} and {locations[1].Path}",
        _ => $"{locations[0].Path} and {locations.Count - 1:N0} more",
    };

    private static string? Sizes(SizeRange sizes) => sizes switch
    {
        { Smallest: { } smallest, Largest: { } largest } => $"{FreeSpace.Format(smallest)} to {FreeSpace.Format(largest)}",
        { Smallest: { } smallest } => $"{FreeSpace.Format(smallest)} or larger",
        { Largest: { } largest } => $"up to {FreeSpace.Format(largest)}",
        _ => null,
    };

    /// <summary>The extension filter in words, or null where it reads none or cannot be searched with, which the page says beside the Search button.</summary>
    private static string? Extensions(ExtensionFilter filter) => filter switch
    {
        { WhyRefused: not null } or { Extensions.Count: 0 } => null,
        { Mode: ExtensionFilterMode.OnlyThese } => "only " + string.Join(", ", filter.Extensions),
        { Mode: ExtensionFilterMode.SkipThese } => "not " + string.Join(", ", filter.Extensions),
        _ => null,
    };
}
