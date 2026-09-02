using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The two sentences only the .NET intermediate-output provider can say, because only it asks git
/// for a second opinion. Everything else it tells the user comes from
/// <see cref="SourceTreePlanNotes"/>.
/// </summary>
internal static class ObjPlanNotes
{
    /// <summary>The git findings, in the order they are shown, or empty if git said nothing.</summary>
    public static IReadOnlyList<PlanNote> ForGit(int trackedCount, int uncheckedCount)
    {
        var notes = new List<PlanNote>(2);

        if (trackedCount > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, Tracked(trackedCount)));
        }

        if (uncheckedCount > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, Unchecked(uncheckedCount)));
        }

        return notes;
    }

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
