using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The sentences only the .NET intermediate-output provider can say, because only it asks git
/// for a second opinion. Everything else it tells the user comes from
/// <see cref="SourceTreePlanNotes"/>.
/// </summary>
internal static class ObjPlanNotes
{
    /// <summary>
    /// The git findings, in the order they are shown, or empty if git was asked and found nothing
    /// to report.
    /// </summary>
    public static IReadOnlyList<PlanNote> ForGit(int trackedCount, int uncheckedCount, int unaskedCount)
    {
        var notes = new List<PlanNote>(3);

        if (trackedCount > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, Tracked(trackedCount)));
        }

        if (uncheckedCount > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, Unchecked(uncheckedCount)));
        }

        if (unaskedCount > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, Unasked(unaskedCount)));
        }

        return notes;
    }

    /// <summary>
    /// Said out loud for the same reason §5.5 makes the discovery fallback observable: a plan
    /// smaller than expected should carry its own explanation rather than leave the user to infer
    /// one. Git was installed and did not answer, or the repository holding a directory could not be
    /// reached to ask it — so the directories in question were left alone, and saying nothing would
    /// make a safeguard that could not run look like a safeguard that found nothing.
    /// </summary>
    private static string Unchecked(int count) => count == 1
        ? "1 directory could not be checked against git, so it was left alone."
        : $"{count} directories could not be checked against git, so they were left alone.";

    /// <summary>
    /// Information rather than a warning, because nothing was left alone and nothing went wrong: the
    /// recognition rule decided, as it does for every other provider. It is said at all because a
    /// plan where the check never ran would otherwise read the same as one where it ran and found
    /// nothing tracked.
    /// </summary>
    private static string Unasked(int count) => count == 1
        ? "Git could not be found, so 1 directory inside a repository was not checked for tracked files. Its project files alone confirmed it as build output."
        : $"Git could not be found, so {count} directories inside a repository were not checked for tracked files. Their project files alone confirmed them as build output.";

    private static string Tracked(int count) => count == 1
        ? "1 directory is tracked in git, so despite looking like build output it holds committed files and was left alone."
        : $"{count} directories are tracked in git, so despite looking like build output they hold committed files and were left alone.";
}
