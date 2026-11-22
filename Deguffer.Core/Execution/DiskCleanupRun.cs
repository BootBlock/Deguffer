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
        StepClock clock,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // Windows measures before it purges, by walking the same directories, and only the purge
        // deletes. So the handler starts measuring now and waits for the look before its purge.
        // Measured elevated on Windows 11 over an uncached 4 GB Windows.old, the step took less time
        // this way in five runs of six: the handler measured faster beside the look than alone,
        // probably because the look's parallel walk reads the tree's metadata ahead of its own.
        using var mayPurge = new PurgeDecision();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stopHandler = stop.Token;
        var hosted = Task.Run(() => handlers.Run(step.Handler, step.Volume, mayPurge, stopHandler), stopHandler);

        var passed = false;

        try
        {
            // Asked of the disk immediately before Windows purges, because Windows clears these whole
            // and the plan's answers are minutes old: an Outlook data file, a file the guard would
            // keep, or a folder Deguffer cannot look inside, arrived since or missed then, stops it.
            // See WholeTreeLook.
            var look = await clock.LookAsync(step.Destroys, keep, ct).ConfigureAwait(false);

            if (look.WhyNot("Nothing was removed", "Windows clears this whole and cannot be told to leave anything", keep) is { } stopped)
            {
                return NotRun(step, stopped, look.Stores.Count);
            }

            ct.ThrowIfCancellationRequested();
            passed = true;
        }
        finally
        {
            if (!passed)
            {
                await LetGoAsync(mayPurge, stop, hosted).ConfigureAwait(false);
            }
        }

        mayPurge.Decide(true);
        var outcome = await hosted.ConfigureAwait(false);

        // From the disk rather than the volume snapshot, for the reason a command step's second
        // reading is: nothing invalidates that snapshot between planning and executing, so it would
        // hand back the figure it is about to be subtracted from.
        var after = await PlanExecutor.MeasureFromDiskAsync(scanner, step.Destroys, default, ct).ConfigureAwait(false);

        // The handler reports its own progress to a sink that passes nothing on, so there is only the
        // end of it to report.
        progress?.Report(1.0);

        // No evidence either way, so the handler's own answer is the only thing to report and nothing
        // is credited, as for a Recycle Bin.
        if (after.Unreached is not null)
        {
            return new StepOutcome(
                step.Description,
                outcome.Ran,
                BytesReclaimed: 0,
                Refusals.None,
                $"{outcome.Message ?? "Windows reported its cleanup finished."} {after.WhyNothingIsCounted}.");
        }

        var remaining = after.Size.Reclaimable;
        var reclaimed = step.EstimatedBytes - remaining;
        var entriesRemoved = Math.Max(0, step.Estimated.Entries - after.Size.Entries);

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

    /// <summary>
    /// Lets the handler go without purging, where the look refused, failed or was cancelled, and waits
    /// for it to return.
    ///
    /// <para><b>Waited for, because no handler may outlive its step.</b> The next step can host a
    /// second instance of the same class, and nothing on the handler's side says two may run at once.
    /// Told to stop, it may not: elevated on Windows 11, <em>Previous Installations</em> was told to
    /// stop 3 s into measuring an uncached <c>Windows.old</c> and returned 22.2 s after it started,
    /// when its measurement was done. So a refused look can take as long as the handler's measurement.</para>
    ///
    /// <para><b>What the handler threw is not the step's answer.</b> Nothing was purged, and the look's
    /// refusal, failure or cancellation is what the step reports.</para>
    /// </summary>
    private static async Task LetGoAsync(PurgeDecision mayPurge, CancellationTokenSource stop, Task hosted)
    {
        mayPurge.Decide(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await hosted.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static StepOutcome NotRun(DiskCleanupStep step, string why, int mailStores) =>
        new(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, why, MailStores: mailStores);
}
