using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What removing a directory's index achieved, before the directory itself is touched. See
/// <see cref="DeleteDirectoryStep.IndexedBy"/>.
/// </summary>
/// <param name="Complete">
/// Whether every index directory is gone. Only then may the directory it indexes lose anything: what is
/// left of an index still names outputs by where they are.
/// </param>
/// <param name="BytesReclaimed">Bytes of the index's files deleted.</param>
/// <param name="Refused">What Windows would not release in the index, by reason.</param>
/// <param name="RefusedFolders">The index's folders Windows refused for a reason of their own.</param>
/// <param name="EntriesRemoved">How many of the index's entries went.</param>
/// <param name="Kept">
/// Files the guard on recently changed files kept, which arrived after the look the run takes first.
/// </param>
/// <param name="MailStores">How many Outlook data files the removal stepped over, and left.</param>
/// <param name="Interrupted">
/// Whether the clean was cancelled while the index was being removed. Never <paramref name="Complete"/>
/// as well: what is left of the index still points into the directory, which then must not be touched.
/// </param>
internal sealed record IndexRemoval(
    bool Complete,
    long BytesReclaimed,
    Refusals Refused,
    FolderRefusals RefusedFolders,
    long EntriesRemoved,
    int Kept,
    int MailStores,
    bool Interrupted = false)
{
    /// <summary>
    /// Remove every index directory of <paramref name="step"/> in turn, recording what each left, and
    /// stop at the first that is not gone completely: the next would be removed for nothing, and the
    /// indexed directory must not be touched.
    ///
    /// <para>Without progress of its own. An index is small beside what it indexes, and the step's bar
    /// belongs to the removal that follows.</para>
    /// </summary>
    public static async Task<IndexRemoval> RemoveAsync(
        DeleteDirectoryStep step,
        MinimumAge keep,
        RefusalRecord refusals,
        RunResidue leftStanding,
        CancellationToken ct)
    {
        var total = new IndexRemoval(Complete: true, 0, Refusals.None, default, 0, 0, 0);

        foreach (var index in step.IndexedBy)
        {
            var removal = await DirectoryRemover.RemoveAsync(index, keep, progress: null, ct).ConfigureAwait(false);

            refusals.Record(index, removal);
            leftStanding.Record(index, removal.LeftStanding);

            // Also true of an index already gone when the run arrived: nothing is left to point anywhere.
            var complete = removal.RootRemoved && !removal.Interrupted;

            total = new IndexRemoval(
                complete,
                total.BytesReclaimed + removal.BytesReclaimed,
                total.Refused + removal.Refused,
                total.RefusedFolders + removal.RefusedFolders,
                total.EntriesRemoved + removal.EntriesRemoved,
                total.Kept + removal.Kept,
                total.MailStores + removal.MailStores.Count,
                removal.Interrupted);

            if (!complete)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// The outcome of a step stopped here, which is a success only where some of the index went and
    /// nothing stopped the removal: that alone costs nothing but a slower next build.
    /// </summary>
    public StepOutcome Stopped(DeleteDirectoryStep step) => new(
        step.Description,
        Succeeded: !Interrupted && (BytesReclaimed > 0 || EntriesRemoved > 0),
        BytesReclaimed,
        Refused,
        Interrupted
            ? $"Stopped when the clean was cancelled, with {LongPath.Display(step.Path)} not yet touched"
              + $"{LeftInPlace.Clauses(Refused, RefusedFolders, Kept, mailStores: MailStores)}. "
              + "Part of its index may already be gone, and what is left of it still points into it."
            : $"Left {LongPath.Display(step.Path)} alone: part of its index could not be removed"
              + $"{LeftInPlace.Clauses(Refused, RefusedFolders, Kept, mailStores: MailStores)}, "
              + "and what is left of the index still points into it.",
        Kept,
        EntriesRemoved: EntriesRemoved,
        RefusedFolders: RefusedFolders,
        MailStores: MailStores,
        Interrupted: Interrupted);
}
