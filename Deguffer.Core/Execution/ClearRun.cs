using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// How <see cref="PlanExecutor"/> carries out a <see cref="ClearDirectoryStep"/>: empty a directory and
/// leave it standing, sparing the entries the plan named.
///
/// <para>The same removal as <see cref="DirectoryStepRun"/> under different bounds, rather than a
/// second walk of its own: §6.3's extended-length paths, §5.3's skip on a refusal, the guard on
/// recently changed files and the refusal to follow a link are all properties of that one removal, and
/// a parallel implementation is where one of them would go missing.</para>
///
/// <para><b>Success is measured differently, because there is no root to have gone.</b> A
/// deletion can point at the directory itself; this one cannot, so what it achieved is bytes,
/// or the guard doing its job. A scratch folder holding nothing but live and recent files
/// reclaims nothing and has failed at nothing.</para>
/// </summary>
/// <param name="refusals">Where the removal records the places Windows refused. See <see cref="PlanExecutor"/>.</param>
/// <param name="fileSystem">What the removal lists and deletes through.</param>
internal sealed class ClearRun(RefusalRecord refusals, IFileSystem fileSystem) : StepRoute<ClearDirectoryStep>
{
    private protected override async Task<StepOutcome> CarryOutAsync(StepRun run, ClearDirectoryStep step, CancellationToken ct)
    {
        var plan = run.Run;

        var removal = await DirectoryRemover.RemoveAsync(
            step.Path,
            run.Keep,
            run.Progress,
            ct,
            fileSystem,
            new RemovalBounds(KeepRoot: true, step.Spared, step.OwnedElsewhere, step.UserWork),
            plan.Time,
            step.Estimated.Entries,
            plan.Deletions).ConfigureAwait(false);

        refusals.Record(step.Path, removal);
        plan.Residue.Record(step.Path, removal.LeftStanding);
        plan.Residue.RecordLeftAlone(step.Path, removal.LeftAlone);

        // A folder Windows refused is a refusal as much as a file is. Without it, a clear whose only
        // outcome was a folder a program is working in would pass as a folder that held nothing.
        var stores = removal.MailStores.Count;

        if (removal.Interrupted)
        {
            return DirectoryStepRun.Stopped(step, removal, stores);
        }

        // A file standing where the folder was is not a folder that held nothing, which is what a clear
        // that met no refusal otherwise reports. Nor is a folder Windows would not let the removal look
        // inside, whose contents nothing tried.
        var unlisted = removal.Unlisted.Count;
        var succeeded = !removal.NotADirectory
            && (removal.BytesReclaimed > 0 || removal.EntriesRemoved > 0 || removal.Kept > 0
                || removal.Spared > 0 || removal.UserWork > 0 || stores > 0
                || (removal.Refused.IsEmpty && removal.RefusedFolders.IsEmpty && unlisted == 0));

        // Asked of the entries as well as the bytes: a folder holding only empty folders reclaims no
        // bytes, and it was still cleared.
        var message = (succeeded, removal.BytesReclaimed, removal.EntriesRemoved) switch
        {
            (false, _, _) when removal.NotADirectory => LeftInPlace.NotAFolder,

            (false, _, _) => LeftInPlace.WhyNothingHappened(removal.Refused, removal.RefusedFolders, unlisted),

            // Nothing came out, and the reasons are the ones the step promised: files too recent to
            // touch, entries something is using, somebody's work, and Outlook mail stores. "Cleared"
            // would be a false statement about a folder that is exactly as full as it was.
            (true, 0, 0) when removal.Kept > 0 || removal.Spared > 0 || removal.UserWork > 0 || stores > 0 =>
                "Nothing was cleared"
                + $"{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, removal.Spared, stores, removal.UserWork, unlisted)}.",

            (true, 0, 0) => "It held nothing to clear.",

            _ => $"Cleared{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, removal.Spared, stores, removal.UserWork, unlisted)}.",
        };

        return new StepOutcome(
            step.Description,
            succeeded,
            removal.BytesReclaimed,
            removal.Refused,
            message,
            removal.Kept,
            removal.Spared,
            removal.EntriesRemoved,
            removal.RefusedFolders,
            stores,
            UserWork: removal.UserWork,
            Unlisted: unlisted)
        {
            Passes = removal.Passes,
        };
    }
}
