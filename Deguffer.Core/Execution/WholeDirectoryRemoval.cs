using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Removes a directory that must not be left in part, or removes nothing at all.
///
/// <para><b>Judged on the walk the removal acts on.</b> Every tree the step destroys is gathered
/// first, its index and then the directory itself, and nothing is deleted until all of them are
/// judged: anything the guard would keep, or a folder that would not be listed, and the removal would
/// leave a part of it standing. A directory that goes with its index is one of these by construction.
/// See <see cref="DeleteDirectoryStep.IsAllOrNothing"/> and <see cref="DeleteDirectoryStep.IndexedBy"/>.
/// Each tree is listed once: a look of its own before the removal listed every folder twice in a row,
/// and a store that arrived between the two listings was stepped over by a removal judged to have
/// none. A store that arrives once the trees are gathered is still never handed to a delete.</para>
///
/// <para>§9 alone for an indivisible directory: the removal would step over a store, or one inside a
/// folder it cannot list, and take what belongs with it. The guard is not asked, because what it keeps
/// can stay. See <see cref="DeleteDirectoryStep.IsIndivisible"/>.</para>
/// </summary>
internal static class WholeDirectoryRemoval
{
    /// <summary>Whether <paramref name="step"/> is removed here rather than as an ordinary directory.</summary>
    public static bool Applies(DeleteDirectoryStep step) => step.GoesWholeOrNotAtAll || step.IsIndivisible;

    /// <param name="refusals">Where each removal records the places Windows refused.</param>
    /// <param name="runResidue">Where each removal records what it left standing, for §5.6.</param>
    public static async Task<StepOutcome> RemoveAsync(
        DeleteDirectoryStep step,
        MinimumAge keep,
        RefusalRecord refusals,
        RunResidue runResidue,
        IProgress<double>? progress,
        IFileSystem fs,
        CancellationToken ct)
    {
        var (guard, how) = step.GoesWholeOrNotAtAll
            ? (keep, "Its parts only mean something together, so it goes whole or not at all")
            : (MinimumAge.Off, "What is inside it goes whole or not at all");

        IReadOnlyList<GatheredRemoval> gathered;

        try
        {
            // The index first and the directory last, the order the removals below take them in.
            gathered = await DirectoryRemover.GatherAsync([.. step.IndexedBy, step.Path], keep, fs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PlanExecutor.Stopped(
                step, new RemovalOutcome(0, Refusals.None, RootRemoved: false) { Interrupted = true }, stores: 0);
        }

        var look = WholeTreeLook.Of(gathered, guarded: step.GoesWholeOrNotAtAll);

        if (look.WhyNot("Nothing was removed", how, guard) is { } partial)
        {
            return new StepOutcome(
                step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, partial, MailStores: look.Stores.Count);
        }

        // The index first, and the directory it indexes only once all of it is gone. See
        // DeleteDirectoryStep.IndexedBy.
        var index = await IndexRemoval.RemoveAsync(
            step, [.. gathered.Take(step.IndexedBy.Count)], refusals, runResidue, fs, ct).ConfigureAwait(false);

        if (!index.Complete)
        {
            return index.Stopped(step);
        }

        var removal = await DirectoryRemover.DeleteAsync(gathered[^1], progress, fs, ct).ConfigureAwait(false);

        refusals.Record(step.Path, removal);
        runResidue.Record(step.Path, removal.LeftStanding);

        // The index went with it, so what the step reports is both. Nothing is added where there is none.
        // A removal stopped part-way adds its index too, because the index went before it began.
        removal = removal with
        {
            BytesReclaimed = removal.BytesReclaimed + index.BytesReclaimed,
            Refused = removal.Refused + index.Refused,
            RefusedFolders = removal.RefusedFolders + index.RefusedFolders,
            EntriesRemoved = removal.EntriesRemoved + index.EntriesRemoved,
            Kept = removal.Kept + index.Kept,
        };

        var stores = removal.MailStores.Count + index.MailStores;

        return removal.Interrupted
            ? PlanExecutor.Stopped(step, removal, stores)
            : PlanExecutor.Deleted(step, removal, stores);
    }
}
