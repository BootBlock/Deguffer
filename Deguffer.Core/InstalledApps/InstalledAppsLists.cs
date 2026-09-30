namespace Deguffer.Core.InstalledApps;

/// <summary>The two lists the page shows, as the reader's choices narrow them.</summary>
public sealed record InstalledAppsLists(IReadOnlyList<InstalledEntry> Stale, IReadOnlyList<InstalledEntry> Installed)
{
    /// <summary>
    /// Split <paramref name="entries"/> into the stale and installed lists (§7.3), each in name
    /// order. A hidden entry is left out unless <paramref name="showHidden"/>, and
    /// <paramref name="filter"/>, where given, keeps only entries whose name, publisher, key or
    /// command holds it.
    /// </summary>
    public static InstalledAppsLists From(IReadOnlyList<InstalledEntry> entries, bool showHidden, string? filter)
    {
        var shown = entries
            .Where(e => showHidden || e.IsListed)
            .Where(e => Matches(e, filter?.Trim()))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Key.Scope)
            .ThenBy(e => e.Key.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new InstalledAppsLists(
            [.. shown.Where(e => e.IsStale)],
            [.. shown.Where(e => !e.IsStale)]);
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
