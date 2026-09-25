using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// How <see cref="PlanExecutor"/> carries out a <see cref="DiskCleanupStep"/>: Windows' own handler
/// clears the directories, and the disk afterwards says what that achieved.
///
/// <para>Apart from the executor's own removals because nothing here is Deguffer's removal. It shares
/// its shape with emptying a Recycle Bin — one call Windows makes whole, then a second reading of the
/// same paths — and not its subject, which is why it is a class of its own rather than a branch of
/// that one.</para>
/// </summary>
internal static class DiskCleanupRun
{
    public static async Task<StepOutcome> RunAsync(
        IDiskCleanupHandlers handlers,
        IDirectoryScanner scanner,
        DiskCleanupStep step,
        MinimumAge keep,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // Windows clears these whole and cannot hold back a recent file, so a guard that would hold
        // something back withdraws the step when the plan is made (CleanupProviderBase.Guarded).
        // Asked again here for the reason EmptyRecycleBinStep's guard is: the cost of that one rule
        // being edited wrongly is the files the user asked to keep, inside the target where §5.6 does
        // not look.
        if (keep.IsOn && step.WithheldRecent)
        {
            return NotRun(
                step,
                $"Nothing was removed: Windows clears this whole, and it holds files changed in the last "
                + $"{keep.Describe()}, which this plan was asked to leave alone.");
        }

        // §9, looked for on the disk immediately before Windows is asked, because the handler cannot be
        // told to leave one file and a store can arrive between the preview and the clean.
        if (await MailStoreSearch.InsideAsync(step.Destroys, ct).ConfigureAwait(false) is { Count: > 0 } stores)
        {
            return NotRun(
                step,
                $"Nothing was removed: this holds an Outlook data file, at {MailStorePlan.Name(stores)}. "
                + "Windows clears it whole, and Deguffer never removes one.",
                stores.Count);
        }

        ct.ThrowIfCancellationRequested();

        var outcome = await Task.Run(() => handlers.Run(step.Handler, step.Volume, ct), ct).ConfigureAwait(false);

        // From the disk rather than the volume snapshot, for the reason a command step's second
        // reading is: nothing invalidates that snapshot between planning and executing, so it would
        // hand back the figure it is about to be subtracted from.
        var after = ScanSize.Zero;
        foreach (var path in step.Destroys)
        {
            after += (await scanner.MeasureFromDiskAsync(path, ct).ConfigureAwait(false)).Size;
        }

        var remaining = after.Reclaimable;
        var reclaimed = step.EstimatedBytes - remaining;
        var entriesRemoved = Math.Max(0, step.Estimated.Entries - after.Entries);

        // The handler reports its own progress to a sink that passes nothing on, so there is only the
        // end of it to report.
        progress?.Report(1.0);

        // The disk is the evidence and the handler's answer only the explanation, as it is for a
        // Recycle Bin: a success taken from the answer alone would repeat a claim the reading above
        // may just have shown to be false.
        var succeeded = remaining == 0 || reclaimed > 0;

        var message = (succeeded, outcome) switch
        {
            (false, { Ran: false, Message: var why }) => $"{why} Everything in it is still there.",

            (false, _) =>
                $"Nothing was removed: Windows reported its cleanup finished, and {FreeSpace.Format(remaining)} "
                + "is still there.",

            // Stopped or refused part of the way through, after taking some of it.
            (true, { Ran: false, Message: var why }) when remaining > 0 =>
                $"{why} {FreeSpace.Format(remaining)} is still there.",

            (true, _) when reclaimed <= 0 => "Cleared; it held nothing by then.",

            (true, _) when remaining > 0 =>
                $"Cleared, apart from {FreeSpace.Format(remaining)} Windows left in place.",

            _ => "Cleared.",
        };

        return new StepOutcome(
            step.Description,
            succeeded,
            Math.Max(0, reclaimed),
            Refusals.None,
            message,
            EntriesRemoved: entriesRemoved);
    }

    private static StepOutcome NotRun(DiskCleanupStep step, string why, int mailStores = 0) =>
        new(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, why, MailStores: mailStores);
}
