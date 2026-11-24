using Deguffer.Core.Text;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// How <see cref="PlanExecutor"/> carries out a <see cref="DeleteDirectoryStep"/>: Deguffer's own
/// removal, which records what Windows refused and what it left standing.
/// </summary>
/// <param name="refusals">Where the removal records the places Windows refused. See <see cref="PlanExecutor"/>.</param>
/// <param name="fileSystem">What the removal lists and deletes through.</param>
internal sealed class DirectoryStepRun(RefusalRecord refusals, IFileSystem fileSystem) : StepRoute<DeleteDirectoryStep>
{
    private protected override async Task<StepOutcome> CarryOutAsync(StepRun run, DeleteDirectoryStep step, CancellationToken ct)
    {
        var plan = run.Run;

        if (WholeDirectoryRemoval.Applies(step))
        {
            return await WholeDirectoryRemoval.RemoveAsync(
                    step, run.Keep, refusals, plan.Residue, run.Progress, fileSystem, plan.Time, plan.Deletions, ct)
                .ConfigureAwait(false);
        }

        var removal = await DirectoryRemover.RemoveAsync(
                step.Path, run.Keep, run.Progress, ct, fileSystem, time: plan.Time, plannedEntries: step.Estimated.Entries, deletions: plan.Deletions)
            .ConfigureAwait(false);

        refusals.Record(step.Path, removal);
        plan.Residue.Record(step.Path, removal.LeftStanding);

        var stores = removal.MailStores.Count;

        return removal.Interrupted ? Stopped(step, removal, stores) : Deleted(step, removal, stores);
    }

    /// <summary>
    /// What a directory step that ran to its end reports, from its removal with its index's figures
    /// already added.
    /// </summary>
    internal static StepOutcome Deleted(DeleteDirectoryStep step, RemovalOutcome removal, int stores)
    {
        // A refusal is not a failure (§5.3). The step only fails if the directory survived intact
        // and nothing at all was reclaimed — that is, we achieved nothing.
        //
        // A file the guard held back counts as something achieved, because it is the outcome the
        // user asked for. A directory holding nothing else reclaims no bytes and keeps its root, so
        // without this the setting working exactly as intended would be reported as a failed step.
        //
        // An empty folder taken out of a folder the guard kept standing is something achieved too, and
        // it reclaims no bytes.
        // An Outlook mail store left where it was is something achieved for the same reason: it is the
        // outcome §9 requires, and a folder holding nothing else would otherwise read as a failure.
        var succeeded = removal.RootRemoved || removal.BytesReclaimed > 0 || removal.Kept > 0
            || removal.EntriesRemoved > 0 || stores > 0;

        var message = succeeded switch
        {
            false when removal.NotADirectory => LeftInPlace.NotAFolder,

            false => LeftInPlace.WhyNothingHappened(removal.Refused, removal.RefusedFolders, removal.Unlisted.Count),

            // Whatever came out was the index, which went whole before the folder was looked at: a
            // removal that met a file at the folder's path takes nothing. "Removed" alone would claim
            // the folder.
            _ when removal.NotADirectory =>
                "Removed its index, and left alone a file that now stands where this folder was.",

            // Nothing came out and the folder is still standing, which is the guard's own case: it
            // held back everything this step named. Saying "Removed" here would be a false statement
            // about the user's disk, and the qualifier would not rescue it — the sentence has to be
            // about what stayed, because that is all that happened.
            _ when removal is { BytesReclaimed: 0, RootRemoved: false, EntriesRemoved: 0 } && stores == 0 =>
                $"Left alone: {Counted.Of(removal.Kept, "file", "files")} changed too recently"
                + $"{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, kept: 0, unlisted: removal.Unlisted.Count)}.",

            // The same shape with a store among what stayed, which the guard's sentence would misname.
            _ when removal is { BytesReclaimed: 0, RootRemoved: false, EntriesRemoved: 0 } =>
                $"Nothing was removed{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, mailStores: stores, unlisted: removal.Unlisted.Count)}.",

            _ => $"Removed{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, mailStores: stores, unlisted: removal.Unlisted.Count)}.",
        };

        return new StepOutcome(
            step.Description,
            succeeded,
            removal.BytesReclaimed,
            removal.Refused,
            message,
            removal.Kept,
            EntriesRemoved: removal.EntriesRemoved,
            RefusedFolders: removal.RefusedFolders,
            MailStores: stores,
            Unlisted: removal.Unlisted.Count)
        {
            Passes = removal.Passes,
        };
    }

    /// <summary>
    /// A removal the clean was cancelled in the middle of, with what it did before it stopped. Never a
    /// success, because the step did not do what it named. A clear reports the same, because it is the
    /// same removal under different bounds.
    /// </summary>
    internal static StepOutcome Stopped(CleanupStep step, RemovalOutcome removal, int stores) => new(
        step.Description,
        Succeeded: false,
        removal.BytesReclaimed,
        removal.Refused,
        (removal.BytesReclaimed == 0 && removal.EntriesRemoved == 0
            ? "Stopped when the clean was cancelled, before anything was removed"
            : "Stopped part-way when the clean was cancelled")
        + $"{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, removal.Spared, stores, removal.UserWork, removal.Unlisted.Count)}.",
        removal.Kept,
        removal.Spared,
        removal.EntriesRemoved,
        removal.RefusedFolders,
        stores,
        Interrupted: true,
        UserWork: removal.UserWork,
        Unlisted: removal.Unlisted.Count)
    {
        Passes = removal.Passes,
    };
}
