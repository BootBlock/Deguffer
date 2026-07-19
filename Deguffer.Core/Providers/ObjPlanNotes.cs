using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The sentences attached to an intermediate-output plan.
///
/// Separate from the provider because it is the only part with no knowledge of directories or
/// tiers: given counts, it produces wording. Discovery over a source root routinely declines
/// hundreds of directories, so what is said about them — and how it is counted — carries more
/// weight here than in a provider whose cache has a handful of children.
/// </summary>
internal static class ObjPlanNotes
{
    public static IReadOnlyList<PlanNote> For(
        ObjDiscovery discovered,
        int declinedCount,
        int trackedCount,
        int uncheckedCount,
        PlanNote? scanNote,
        PlanNote? runningProcesses)
    {
        var notes = new List<PlanNote>(5);

        if (!discovered.UsedIndex)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Searched your source folders directly. Running Deguffer as administrator lets it " +
                "read the volume index instead, which is considerably faster."));
        }

        if (declinedCount > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, Declined(declinedCount)));
        }

        if (trackedCount > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, Tracked(trackedCount)));
        }

        if (uncheckedCount > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, Unchecked(uncheckedCount)));
        }

        if (scanNote is { } scan)
        {
            notes.Add(scan);
        }

        if (runningProcesses is { } warning)
        {
            notes.Add(warning);
        }

        return notes;
    }

    /// <summary>
    /// Counted rather than listed. Every other provider names each child it declined, but a source
    /// root routinely holds hundreds that are not intermediate output and several hundred notes
    /// would bury the ones that matter. They are all carried as protected paths regardless, so the
    /// §5.6 guarantee is unchanged by not naming them here.
    ///
    /// Both grammatical forms are written out. Driving the real window produced "Left 1 directory …
    /// because they could not be confirmed" from a pluralised noun with a fixed pronoun, which is
    /// the sort of thing a developer never sees on a machine with more than one.
    /// </summary>
    private static string Declined(int count) => count == 1
        ? "Left 1 directory named 'obj' alone because it could not be confirmed as .NET intermediate build output."
        : $"Left {count} directories named 'obj' alone because they could not be confirmed as .NET intermediate build output.";

    /// <summary>
    /// Said out loud for the same reason §5.5 makes the discovery fallback observable: a plan
    /// smaller than expected should carry its own explanation rather than leave the user to infer
    /// one. Git was installed and asked, and did not answer — so the directories in question were
    /// left alone, and saying nothing would make a safeguard that could not run look like a
    /// safeguard that found nothing.
    /// </summary>
    private static string Unchecked(int count) => count == 1
        ? "1 directory could not be checked against git, so it was left alone."
        : $"{count} directories could not be checked against git, so they were left alone.";

    private static string Tracked(int count) => count == 1
        ? "1 directory is tracked in git, so despite looking like build output it holds committed files and was left alone."
        : $"{count} directories are tracked in git, so despite looking like build output they hold committed files and were left alone.";
}
