using Deguffer.Core.Viewing;

namespace Deguffer.Core.InstalledApps;

/// <summary>The two lists the page shows, as the reader's choices narrow them.</summary>
/// <param name="StaleEmpty">Why the stale list is empty, or null where it has rows.</param>
public sealed record InstalledAppsLists(
    IReadOnlyList<InstalledEntry> Stale,
    IReadOnlyList<InstalledEntry> Installed,
    EmptyListNote? StaleEmpty)
{
    /// <summary>
    /// Split the entries of <paramref name="reading"/> into the stale and installed lists (§7.3),
    /// each in name order. A hidden entry is left out unless <paramref name="showHidden"/>, and
    /// <paramref name="filter"/>, where given, keeps only entries whose name, publisher, key or
    /// command holds it.
    /// </summary>
    public static InstalledAppsLists From(InstalledAppsReading reading, bool showHidden, string? filter)
    {
        var text = filter?.Trim();
        var shown = reading.Entries
            .Where(e => showHidden || e.IsListed)
            .Where(e => Matches(e, text))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Key.Scope)
            .ThenBy(e => e.Key.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyList<InstalledEntry> stale = [.. shown.Where(e => e.IsStale)];

        return new InstalledAppsLists(
            stale,
            [.. shown.Where(e => !e.IsStale)],
            stale.Count == 0 ? WhyNoneStale(reading, showHidden, text) : null);
    }

    /// <summary>
    /// Why no stale entry is shown. A stale entry the hidden switch keeps back, though the filter
    /// matches it, is named first, because turning the switch on lists it. Any other stale entry
    /// the reading holds is kept back by the filter. Only a reading with none says there are none.
    /// </summary>
    private static EmptyListNote WhyNoneStale(InstalledAppsReading reading, bool showHidden, string? filter)
    {
        var hidden = showHidden ? 0 : reading.Entries.Count(e => e.IsStale && !e.IsListed && Matches(e, filter));

        if (hidden > 0)
        {
            return new EmptyListNote(
                EmptyListCause.Hidden,
                "No stale entries in the list Windows shows",
                $"{Count(hidden, "stale entry is", "stale entries are")} hidden. Turn on Show entries Windows hides to list hidden entries too.");
        }

        if (!string.IsNullOrEmpty(filter) && reading.Entries.Any(e => e.IsStale))
        {
            return new EmptyListNote(
                EmptyListCause.Filtered,
                "No stale entry matches the filter",
                "Change or clear the filter to see the rest.");
        }

        return new EmptyListNote(
            EmptyListCause.Nothing,
            "No stale entries",
            reading.RefusedScopes.Count == 0
                ? "Every entry belongs to a program that is still there, or that nothing proves gone."
                : "Every entry Deguffer could read belongs to a program that is still there, or that nothing proves gone. "
                  + $"Windows would not open {string.Join(" or ", reading.RefusedScopes.Select(s => s.PhysicalPath()))}, so entries there were not read.");
    }

    /// <summary>What the reading found, in one sentence, with the keys Windows would not open named.</summary>
    public static string Headline(InstalledAppsReading reading)
    {
        var listed = reading.Entries.Where(e => e.IsListed).ToList();
        var stale = listed.Count(e => e.IsStale);
        var hidden = reading.Entries.Count - listed.Count;
        var hiddenStale = reading.Entries.Count(e => !e.IsListed && e.IsStale);

        var headline = $"{Count(stale, "stale entry", "stale entries")} and {Count(listed.Count - stale, "installed program", "installed programs")} "
            + $"in the list Windows shows. {Count(hidden, "more entry is", "more entries are")} hidden"
            + (hiddenStale > 0 ? $", {hiddenStale} of them stale." : ".");

        return reading.RefusedScopes.Count == 0
            ? headline
            : $"{headline} Windows would not open {string.Join(" or ", reading.RefusedScopes.Select(s => s.PhysicalPath()))}, "
              + "so entries there are not shown.";
    }

    private static bool Matches(InstalledEntry entry, string? filter) =>
        string.IsNullOrEmpty(filter)
        || entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || entry.Publisher?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true
        || entry.Key.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || entry.Command.Text.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string Count(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
