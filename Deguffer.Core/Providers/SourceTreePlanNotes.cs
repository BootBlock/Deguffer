using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The sentences attached to a plan built by searching the user's own source folders.
///
/// Separate from the providers because it is the only part with no knowledge of directories or
/// tiers: given counts, it produces wording. Discovery over a source root routinely declines
/// hundreds of directories, so what is said about them — and how it is counted — carries more
/// weight here than in a provider whose cache has a handful of children.
///
/// Shared across every such provider rather than written per provider. Two of these notes are the
/// visible half of a safety rule — that a search fell back to walking, and that a project was held
/// back because something is using it — and a rule whose wording is copied is a rule that ends up
/// said in one place and not in another.
/// </summary>
internal static class SourceTreePlanNotes
{
    /// <param name="directoryNames">The names searched for, already quoted, as the user would read them.</param>
    /// <param name="subject">What a recognised one is, to complete "could not be confirmed as …".</param>
    /// <param name="extra">Notes belonging to one provider's own checks, in the order it wants them.</param>
    public static IReadOnlyList<PlanNote> For(
        SourceDiscovery discovered,
        string directoryNames,
        string subject,
        int declinedCount,
        LiveTreeVetoResult live,
        PlanNote? scanNote,
        PlanNote? runningProcesses,
        IReadOnlyList<PlanNote>? extra = null)
    {
        var notes = new List<PlanNote>(6);

        // §5.5 requires the fallback to be observable, and this is the discovery half of it — the
        // measurement half is scanNote. On an unelevated run both took the slow route, and saying so
        // twice in two wordings reads as a defect rather than as precision, so the sentence is left
        // to the note that is already there. Where measuring took the fast path and only discovery
        // walked, this is the only thing that would say so, which is why it is not simply dropped.
        if (!discovered.UsedIndex && scanNote is null)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Searched your source folders directly. Running Deguffer as administrator lets it " +
                "read the volume index instead, which is considerably faster."));
        }

        if (declinedCount > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, Declined(declinedCount, directoryNames, subject)));
        }

        if (extra is not null)
        {
            notes.AddRange(extra);
        }

        if (LiveTreeVeto.NoteFor(live.Vetoed) is { } vetoed)
        {
            notes.Add(vetoed);
        }

        if (LiveTreeVeto.IncompleteNote(live.Complete) is { } incomplete)
        {
            notes.Add(incomplete);
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
    /// root routinely holds hundreds that are not build output and several hundred notes would bury
    /// the ones that matter. They are all carried as protected paths regardless, so the §5.6
    /// guarantee is unchanged by not naming them here.
    ///
    /// Both grammatical forms are written out. Driving the real window produced "Left 1 directory …
    /// because they could not be confirmed" from a pluralised noun with a fixed pronoun, which is
    /// the sort of thing a developer never sees on a machine with more than one.
    /// </summary>
    private static string Declined(int count, string directoryNames, string subject) => count == 1
        ? $"Left 1 directory named {directoryNames} alone because it could not be confirmed as {subject}."
        : $"Left {count} directories named {directoryNames} alone because they could not be confirmed as {subject}.";
}
