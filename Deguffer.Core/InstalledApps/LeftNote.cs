namespace Deguffer.Core.InstalledApps;

/// <summary>
/// What a selection says about the entries its action will leave (§7.3), worded once for both lists
/// so a refusal reads the same wherever it is met.
/// </summary>
internal static class LeftNote
{
    /// <param name="acted">How many of the selected entries the action will act on.</param>
    /// <param name="plural">What the entries are called, such as "entries" or "programs".</param>
    /// <returns>The note, or null where nothing will be left.</returns>
    public static string? For(IReadOnlyList<(InstalledEntry Entry, ActionVerdict Verdict)> refused, int acted, string plural) => refused switch
    {
        [] => null,

        // Nothing else is selected, so the refusal alone says what will happen.
        [var only] when acted == 0 => only.Verdict.Reason,
        [var only] => $"'{only.Entry.Name}' will be left. {only.Verdict.Reason}",
        _ => $"{refused.Count} of the selected {plural} will be left. '{refused[0].Entry.Name}': {refused[0].Verdict.Reason}",
    };
}
