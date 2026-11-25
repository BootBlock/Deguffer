using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// How <see cref="PlanExecutor"/> carries out a <see cref="RunCommandStep"/>: §5.1's preferred route,
/// the tool's own eviction command, checked again immediately before it runs and measured afterwards.
/// </summary>
/// <param name="runner">What starts the tool.</param>
/// <param name="inspector">
/// Asked whether the program a command <see cref="RunCommandStep.RunsOnlyWhile"/> still runs.
/// </param>
/// <param name="updateHold">
/// Asked again after the tool's own measurement, because an update that starts during it is what the
/// hold exists for.
/// </param>
internal sealed class CommandRun(IProcessRunner runner, IProcessInspector inspector, UpdateHoldAtClean updateHold)
    : StepRoute<RunCommandStep>
{
    private protected override async Task<StepOutcome> CarryOutAsync(StepRun run, RunCommandStep step, CancellationToken ct)
    {
        var runActivity = run.Run.Activity;
        var underway = run.Run.Underway;

        // The "before" size was measured when the plan was built; re-walking a multi-gigabyte
        // tree to learn it again would double the cost of the operation.
        //
        // This is sound only because a plan-time figure is always freshly measured. Remembered
        // sizes exist (ScanEstimateCache) but are never returned from a measurement — they only
        // reach the screen while the real scan runs. Were a stale one to arrive here it would not
        // merely look wrong: it would inflate the reclaimed total reported below.
        //
        // MeasuredBefore wins where it is present, because the delta must subtract like from
        // like: a step whose estimate is the tool's own figure re-measures paths that never held
        // that number. See its declaration.
        //
        // The two sides are commensurable because ScanSize.Reclaimable is Logical, which both of
        // §5.5's routes measure and measure identically. This subtraction was the first place that
        // mattered — the before-figure can come from the file table and the after-figure never does
        // — and it is no longer a special case: see ScanSize.Reclaimable for why the whole tool
        // reports that axis.
        var before = (step.MeasuredBefore ?? step.Estimated).Reclaimable;

        // Asked of a process table within the use checks' limit rather than of the snapshot the
        // planning pass took: the user can close the program while the preview is on screen, and the
        // tool would then start it.
        if (step.RunsOnlyWhile.Count > 0)
        {
            inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

            if (inspector.FindRunning(step.RunsOnlyWhile).Count == 0)
            {
                return new StepOutcome(
                    step.Description,
                    Succeeded: false,
                    BytesReclaimed: 0,
                    Refusals.None,
                    $"Not run: {step.RunsOnlyWhile[0]} is no longer running, and this command would start it. "
                    + $"Open {step.RunsOnlyWhile[0]} and clean again.");
            }
        }

        // Asked before the look so a command already refused costs no walk, and again once the look
        // and any measurement are done.
        if (step.TargetCheck is { } target && await target.WhyNotAsync(step, ct).ConfigureAwait(false) is { } retargeted)
        {
            return Retargeted(step, retargeted);
        }

        // §9, looked for again on the disk immediately before the tool runs, because the tool cannot be
        // told to leave one file and a store can arrive between the preview and the clean. A folder
        // Windows will not list could hold one. See WholeTreeLook for what the look costs and why it is
        // paid here.
        var look = await run.Clock.LookAsync(step.MeasuredPaths, MinimumAge.Off, ct).ConfigureAwait(false);

        if (look.WhyNot("Not run", "The tool cannot be told to leave anything", MinimumAge.Off) is { } held)
        {
            return new StepOutcome(
                step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, held, MailStores: look.Stores.Count);
        }

        if (look.WhyNotThroughLinks("Not run") is { } linked)
        {
            return new StepOutcome(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, linked);
        }

        // A tool that states its own figure is asked for it immediately before the command as well as
        // after, rather than trusted from the plan. What it describes can change without Deguffer:
        // Windows cleans its component store on a schedule, and another row in the same clean can run a
        // command over the same store. The plan's figure would then credit this command with a reclaim
        // it did not make.
        var measuredBefore = step.MeasuredBy is { } asked
            ? await asked.MeasureAsync(MeasuredWhen.BeforeCommand, runActivity, ct).ConfigureAwait(false)
            : before;

        // Asked again, because the tool's measurement can take a minute, and an update that starts
        // during it is exactly what the hold exists for. The servicing stack the measurement itself
        // woke is not that update, where the step names the record it was made in: see
        // CleanupStep.ServicingRecord and UpdateHoldAtClean.
        if (step is { HeldWhileUpdating: true, MeasuredBy: not null } && await updateHold.WhyHeldAsync(step, ct).ConfigureAwait(false) is { } updating)
        {
            return new StepOutcome(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, updating);
        }

        // Asked again, because the look walks every path the command clears and the tool's measurement
        // can take a minute, and what a target check guards against happens at the machine's pace, not
        // the run's: a build started, or a handle given to another item, while either ran.
        if (step.TargetCheck is { } targetSince && await targetSince.WhyNotAsync(step, ct).ConfigureAwait(false) is { } retargetedSince)
        {
            return Retargeted(step, retargetedSince);
        }

        var tool = ToolRun.Command(step.WhenCancelled).WritingIn(step.Output).Without(step.Unset);

        // Around the tool alone, rather than the whole step: the look and the measurement above stop
        // when the clean is cancelled, and only the tool can hold the run past a press of Cancel.
        underway?.Report(CommandUnderway.Of(step));

        CommandOutcome outcome;

        // The command is the whole of what this step writes, so a tool's reading taken before it never
        // stands for the reading after it.
        using (runActivity.Begin())
        {
            try
            {
                outcome = step.ServicingCalls is { } calls
                    ? await calls.MakeAsync(() => runner.RunAsync(step.FileName, step.Arguments, tool, ct)).ConfigureAwait(false)
                    : await runner.RunAsync(step.FileName, step.Arguments, tool, ct).ConfigureAwait(false);
            }
            finally
            {
                underway?.Report(null);
            }
        }

        // A command that returned has run to its end, whether it was left to finish or ended just as the
        // clean was cancelled, so it is reported as run rather than as stopped. What it freed is not
        // measured, because the measurement is more work the user asked to stop, and a tool's own
        // measurement can take minutes.
        if (ct.IsCancellationRequested)
        {
            var why = step.WhenCancelled is CancelledCommand.Finishes
                ? "the clean was cancelled while this command ran, and it was left to finish because stopping it part-way could do harm"
                : "the clean was cancelled as this command finished";

            return new StepOutcome(
                step.Description, outcome.Succeeded, BytesReclaimed: 0, Refusals.None, $"{outcome.Message} ({why}, so what it freed is not counted)");
        }

        if (outcome.Succeeded && step is { Removes: { } item, Scheduled: { } scheduled })
        {
            switch (await RemovalMarking.AwaitAsync(item, scheduled, run.Run.Time, ct).ConfigureAwait(false))
            {
                case RemovalMark.Marked:
                    return new StepOutcome(
                        step.Description,
                        Succeeded: true,
                        BytesReclaimed: 0,
                        Refusals.None,
                        $"{scheduled.Remover} removes it the next time it starts.",
                        BytesScheduled: before);

                case RemovalMark.Neither:
                    return new StepOutcome(
                        step.Description,
                        Succeeded: false,
                        BytesReclaimed: 0,
                        Refusals.None,
                        $"{scheduled.Remover} reported success, but it neither removed {LongPath.Display(item)} "
                        + "nor marked it for removal, so nothing will happen to it.");

                case RemovalMark.Unseen:
                    return new StepOutcome(
                        step.Description,
                        Succeeded: false,
                        BytesReclaimed: 0,
                        Refusals.None,
                        $"{scheduled.Remover} reported success, but Windows would not say whether it marked "
                        + $"{LongPath.Display(item)} for removal, so nothing shows whether it will be removed.");
            }

            // Removed: the tool took the item at once after all, which the measurement below reports.
        }

        // From the disk, never from the volume snapshot. Nothing invalidates that snapshot between
        // planning and executing — Invalidate runs once, at the top of a planning pass — so an
        // ordinary measurement here would hand back the very figure it is about to be subtracted
        // from, and a clean that freed gigabytes would report nothing.
        //
        // A tool that states its own figure is asked for it instead, because the disk is not where
        // that figure came from. See IToolMeasurement.
        long? measured;

        if (step.MeasuredBy is { } measurement)
        {
            var when = outcome.Succeeded ? MeasuredWhen.AfterCommandSucceeded : MeasuredWhen.AfterCommandFailed;
            measured = await measurement.MeasureAsync(when, runActivity, ct).ConfigureAwait(false);
        }
        else
        {
            var reading = await DiskReading.ReadAsync(run.Run.Scanner, step.MeasuredPaths, default, ct).ConfigureAwait(false);

            if (reading.Unreached is not null)
            {
                return new StepOutcome(
                    step.Description,
                    outcome.Succeeded,
                    BytesReclaimed: 0,
                    Refusals.None,
                    $"{outcome.Message} ({reading.WhyNothingIsCounted})");
            }

            measured = reading.Size.Reclaimable;
        }

        // Nothing is counted rather than the whole estimate: a figure nobody checked is the one this
        // subtraction exists to avoid reporting.
        if (measured is not { } after || measuredBefore is not { } start)
        {
            return new StepOutcome(
                step.Description,
                outcome.Succeeded,
                BytesReclaimed: 0,
                Refusals.None,
                $"{outcome.Message} (the tool did not say what its cache held "
                + (measuredBefore is null ? "before the command" : "afterwards")
                + ", so nothing is counted as freed)");
        }

        var reclaimed = start - after;

        // A negative delta means the tree grew between the start figure and the end — a build
        // restoring packages in the background, most likely. A tool asked immediately before its
        // command grew while the command ran, and a disk measurement grew since the scan. Report
        // what is actually still there rather than clamping to zero and claiming nothing was
        // reclaimed.
        var message = reclaimed < 0
            ? $"{outcome.Message} (the cache grew "
              + (step.MeasuredBy is null ? "since the scan" : "while the command ran")
              + $"; {FreeSpace.Format(after)} remains)"
            : outcome.Message;

        return new StepOutcome(
            step.Description,
            outcome.Succeeded,
            BytesReclaimed: Math.Max(0, reclaimed),
            Refusals.None,
            message);
    }

    /// <summary>A command not run because its <see cref="RunCommandStep.TargetCheck"/> said why not.</summary>
    private static StepOutcome Retargeted(RunCommandStep step, string why) =>
        new(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, $"Not run: {why}.");
}
