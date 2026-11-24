using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>How <see cref="PlanExecutor"/> carries out a <see cref="DeleteFileStep"/>: Deguffer removes the one file itself.</summary>
/// <param name="refusals">Where the removal records whether Windows refused the file. See <see cref="PlanExecutor"/>.</param>
/// <param name="fileSystem">What the removal deletes through.</param>
internal sealed class FileStepRun(RefusalRecord refusals, IFileSystem fileSystem) : StepRoute<DeleteFileStep>
{
    private protected override async Task<StepOutcome> CarryOutAsync(StepRun run, DeleteFileStep step, CancellationToken ct)
    {
        var removal = await FileRemover.RemoveAsync(step.Path, run.Keep, ct, fileSystem, run.Run.Deletions).ConfigureAwait(false);

        // One file, so there is no fraction to report along the way — only the end of it.
        run.Progress?.Report(1.0);

        // Kept is a success for the same reason it is on a directory: nothing was removed because
        // nothing was meant to be. There is only the one file, so the whole message says so rather
        // than qualifying a removal that did not happen.
        //
        // The record is left as it was, because nothing asked Windows anything: a refusal recorded
        // by an earlier clean is still the latest answer there is.
        if (removal.Kept)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: true,
                BytesReclaimed: 0,
                Refusals.None,
                "Left alone: it changed too recently.",
                Kept: 1);
        }

        // The same success for the same reason: a store is never removed, so leaving it is the step
        // doing what it must. Nothing asked Windows anything here either, so the record stays as it
        // was.
        if (removal.MailStore)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: true,
                BytesReclaimed: 0,
                Refusals.None,
                "Left alone: it is an Outlook data file, and Deguffer never removes one.",
                MailStores: 1);
        }

        refusals.Replace(step.Path, removal.Refused.IsEmpty ? [] : [step.Path]);

        var message = removal.Removed ? "Removed." : LeftInPlace.WhyNothingHappened(removal.Refused, FolderRefusals.None);

        return new StepOutcome(
            step.Description,
            removal.Removed,
            removal.BytesReclaimed,
            removal.Refused,
            message,
            EntriesRemoved: removal.Took ? 1 : 0);
    }
}
