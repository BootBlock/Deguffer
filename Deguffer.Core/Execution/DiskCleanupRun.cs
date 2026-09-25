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
        // Asked of the disk immediately before Windows is, because Windows clears these whole and the
        // plan's answers are minutes old: an Outlook data file, a file the guard would keep, or a folder
        // Deguffer cannot look inside, arrived since or missed then, stops it. See WholeTreeLook.
        var look = await WholeTreeLook.TakeAsync(step.Destroys, keep, ct).ConfigureAwait(false);

        if (look.WhyNot("Windows clears this whole and cannot be told to leave anything", keep) is { } stopped)
        {
            return NotRun(step, stopped, look.Stores.Count);
        }

        ct.ThrowIfCancellationRequested();

        var outcome = await Task.Run(() => handlers.Run(step.Handler, step.Volume, ct), ct).ConfigureAwait(false);

        // From the disk rather than the volume snapshot, for the reason a command step's second
        // reading is: nothing invalidates that snapshot between planning and executing, so it would
        // hand back the figure it is about to be subtracted from.
        var after = await PlanExecutor.MeasureFromDiskAsync(scanner, step.Destroys, ct).ConfigureAwait(false);

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
            (false, { Message: { } why }) => $"{why} Everything in it is still there.",

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

    private static StepOutcome NotRun(DiskCleanupStep step, string why, int mailStores) =>
        new(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, why, MailStores: mailStores);
}
