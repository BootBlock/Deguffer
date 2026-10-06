using System.Diagnostics;
using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.SystemProtection;

namespace Deguffer.Core.Execution;

/// <summary>
/// Carries out a plan. Holds no knowledge of any cache — it dispatches the steps a provider
/// already decided on, and reports what happened.
/// </summary>
/// <param name="refusals">
/// Where each removal records the places Windows refused, so the next preview of the same location
/// can leave out what is still refused. Required rather than defaulted: the default would be the
/// signed-in user's own record, and a caller that forgot to pass one would write into it.
/// </param>
/// <param name="emptier">
/// How an <see cref="EmptyRecycleBinStep"/> is carried out. Null where the plans this executor runs
/// hold none, and a plan that holds one is then refused before any of its steps runs.
///
/// <para><b>Never defaulted to the real one.</b> It empties the Recycle Bin of whoever runs the
/// process, so a test that forgot to pass a fake would be one broken guard away from doing that. The
/// real one is given only where the app composes its providers,
/// <see cref="CleanupPlanner.CreateDefault"/>.</para>
/// </param>
/// <param name="cloud">
/// How a <see cref="ReleaseLocalCopiesStep"/> is carried out and its files proved standing. Null, and
/// never defaulted, for the reasons <paramref name="emptier"/> is: the real one acts on the cloud
/// accounts of whoever runs the process.
/// </param>
/// <param name="handlers">
/// How a <see cref="DiskCleanupStep"/> is carried out. Null, and never defaulted, for the reasons
/// <paramref name="emptier"/> is: the real one can delete the previous Windows installation.
/// </param>
/// <param name="servicing">
/// Asked again, with <paramref name="inspector"/>, immediately before a step that is
/// <see cref="CleanupStep.HeldWhileUpdating"/>, and before a command that
/// <see cref="RunCommandStep.RunsOnlyWhile"/> a program runs. Defaulted to the machine's own, because
/// it only reads.
/// </param>
/// <param name="time">
/// The clock a command waits on while its tool marks what it will remove later. See
/// <see cref="ScheduledRemoval"/>. Defaulted to the system clock, and injected so a test of a tool
/// that never marks anything does not wait out the real interval.
/// </param>
/// <param name="environment">
/// The account whose own folders no step may destroy. See <see cref="StandingFolders"/>. Defaulted
/// to the signed-in account, which is the one a provider's plan was made for.
/// </param>
/// <param name="system">The directories Windows is built out of, for the same check.</param>
/// <param name="protection">
/// How a <see cref="RemoveRestorePointsStep"/> is carried out and proved. Null, and never defaulted, for
/// the reasons <paramref name="emptier"/> is: the real one removes the machine's restore points.
/// </param>
public sealed class PlanExecutor(
    IProcessRunner runner,
    IDirectoryScanner scanner,
    RefusalRecord refusals,
    IRecycleBinEmptier? emptier = null,
    ICloudFiles? cloud = null,
    IDiskCleanupHandlers? handlers = null,
    IWindowsServicing? servicing = null,
    IProcessInspector? inspector = null,
    TimeProvider? time = null,
    IUserEnvironment? environment = null,
    ISystemDirectories? system = null,
    ISystemProtection? protection = null)
{
    /// <summary>
    /// How long a command's tool is given to mark the item it will remove later. LM Studio answers
    /// <c>lms</c> before it writes its marker, so the first look can come too early, and the write
    /// itself takes milliseconds. Long enough for a machine under load, and short enough that a tool
    /// that never marks anything costs the run a few seconds rather than a hang.
    /// </summary>
    private static readonly TimeSpan MarkingWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MarkingPoll = TimeSpan.FromMilliseconds(250);

    private readonly IRecycleBinEmptier? _emptier = emptier;
    private readonly ICloudFiles? _cloud = cloud;
    private readonly IDiskCleanupHandlers? _handlers = handlers;
    private readonly ISystemProtection? _protection = protection;
    private readonly IWindowsServicing _servicing = servicing ?? WindowsServicing.Current;
    private readonly IProcessInspector _inspector = inspector ?? ProcessInspector.Default;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IUserEnvironment _environment = environment ?? UserEnvironment.Current;
    private readonly ISystemDirectories _system = system ?? SystemDirectories.Current;

    /// <param name="runReach">
    /// What the whole run may destroy. §5.6's negative is answered against it rather than against
    /// this plan alone, because a run is many plans and a folder another provider deleted is not a
    /// folder something outside Deguffer deleted. Null means this plan is the whole run.
    /// </param>
    /// <param name="residue">
    /// What the run's removals have left standing and left alone, which each removal here adds to
    /// and §5.6 then reads — see <see cref="RunResidue"/>. Null means this plan is the whole run, and it is given a
    /// record of its own.
    /// </param>
    /// <param name="ct">
    /// Stops the plan between steps, and inside a step that can stop. <b>Never thrown:</b> the result
    /// holds the steps that ran, is <see cref="CleanupResult.Interrupted"/>, and is verified like any
    /// other, because what a cancelled clean already took is what §5.6 has to answer for.
    /// </param>
    public async Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        RunReach? runReach,
        RunResidue? residue,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // Before anything runs, so a plan this executor cannot finish removes nothing rather than half
        // of itself.
        if (plan.Steps.FirstOrDefault(step => !HasRouteFor(step)) is { } unroutable)
        {
            throw new InvalidOperationException(
                $"The executor for '{plan.ProviderId}' was given no way to carry out a {unroutable.GetType().Name}.");
        }

        var runResidue = residue ?? new RunResidue();
        var stopwatch = Stopwatch.StartNew();
        var outcomes = new List<StepOutcome>(plan.Steps.Count);
        var heldAtClean = new List<ProtectedPath>();

        // The same weighting the planner applies to whole plans, for the same reason: one obj
        // directory of 4 GB and five of 20 MB are six steps, and splitting the bar six ways would
        // crawl through the first sixth and then jump the rest.
        var weights = ProgressWeights.For(plan.Steps.Select(s => s.EstimatedBytes));
        var total = weights.Sum();
        var done = 0.0;

        // Set when the clean is cancelled before every step has run to its end.
        var interrupted = false;

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            // Stopped here rather than thrown, so what the steps before this one did is still verified
            // below.
            if (ct.IsCancellationRequested)
            {
                interrupted = true;
                break;
            }

            // Each step's own 0-to-1 becomes its slice of this plan's 0-to-1.
            var stepProgress = ScaledProgress.Within(progress, done / total, weights[i] / total);

            StepOutcome outcome;

            try
            {
                outcome = await RunStepAsync(plan, plan.Steps[i], heldAtClean, runResidue, stepProgress, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A step that throws when cancelled cannot say what it did first. A tool's command can
                // have removed part of its cache, and nothing counted it.
                outcome = new StepOutcome(
                    plan.Steps[i].Description,
                    Succeeded: false,
                    BytesReclaimed: 0,
                    Refusals.None,
                    "Stopped when the clean was cancelled. What it had removed by then is not counted.",
                    Interrupted: true);
            }

            outcomes.Add(outcome);

            if (outcome.Interrupted)
            {
                interrupted = true;
                break;
            }

            // Reported from here rather than trusted from the step: a command step reports nothing
            // at all while it runs, and a removal that ends early would leave a gap that never
            // closes.
            done += weights[i];
            progress?.Report(done / total);
        }

        stopwatch.Stop();

        return new CleanupResult
        {
            ProviderId = plan.ProviderId,
            ProviderName = plan.ProviderName,
            Steps = outcomes,
            Duration = stopwatch.Elapsed,
            Interrupted = interrupted,

            // §5.6 is not a separate user action: acting and proving what survived are one step. What a
            // use check held back is proved standing with everything the plan protected.
            //
            // Never cancelled, because a cancelled clean is the one that most needs it: something has
            // gone, and the user chose where it stopped without knowing what it had reached.
            Verification = PlanVerifier.Verify(
                heldAtClean.Count == 0 ? plan : plan with { ProtectedPaths = [.. plan.ProtectedPaths, .. heldAtClean] },
                runReach,
                runResidue,
                CancellationToken.None,
                _cloud,
                _protection),
        };
    }

    /// <summary>
    /// Carry out one step, or say why it did not run. What a use check holds back is added to
    /// <paramref name="heldAtClean"/>, because it is proved standing afterwards.
    /// </summary>
    private async Task<StepOutcome> RunStepAsync(
        CleanupPlan plan,
        CleanupStep step,
        List<ProtectedPath> heldAtClean,
        RunResidue runResidue,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (step.HeldWhileUpdating && StillUpdating(step) is { } updating)
        {
            return new StepOutcome(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, updating);
        }

        if (step is DeleteStep standing && WhyNotTaken(standing) is { } refused)
        {
            return new StepOutcome(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, refused);
        }

        // §5.3 asked again: the plan's answer about what is in use is as old as the preview.
        if (step is DeleteStep deletion)
        {
            var recheck = UseRecheck.Of(deletion, ct);
            heldAtClean.AddRange(recheck.Survivors);

            if (recheck.Step is null)
            {
                return new StepOutcome(
                    step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, $"Nothing was removed: {recheck.Withheld}.");
            }

            step = recheck.Step;
        }

        return step switch
        {
            RunCommandStep command => await RunCommandAsync(command, ct).ConfigureAwait(false),
            ClearDirectoryStep clear => await ClearAsync(clear, plan.Keep, runResidue, progress, ct).ConfigureAwait(false),
            DeleteDirectoryStep delete => await DeleteAsync(delete, plan.Keep, runResidue, progress, ct).ConfigureAwait(false),
            DeleteFileStep delete => await DeleteAsync(delete, plan.Keep, progress, ct).ConfigureAwait(false),
            EmptyRecycleBinStep empty => await EmptyAsync(empty, plan.Keep, progress, ct).ConfigureAwait(false),
            DiskCleanupStep handler => await DiskCleanupRun.RunAsync(_handlers!, scanner, handler, plan.Keep, progress, ct).ConfigureAwait(false),
            ReleaseLocalCopiesStep release => await LocalCopyRelease.RunAsync(_cloud!, release, plan.Keep, progress, ct).ConfigureAwait(false),
            RemoveRestorePointsStep removal => await RestorePointRun.RunAsync(_protection!, removal, progress, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unknown step type {step.GetType().Name}."),
        };
    }

    /// <summary>Whether this executor was given the route <paramref name="step"/> is carried out by.</summary>
    private bool HasRouteFor(CleanupStep step) => step switch
    {
        EmptyRecycleBinStep => _emptier is not null,
        DiskCleanupStep => _handlers is not null,
        ReleaseLocalCopiesStep => _cloud is not null,
        RemoveRestorePointsStep => _protection is not null,
        _ => true,
    };

    /// <summary>
    /// Why <paramref name="step"/> must not run because Windows is now in the middle of an update, or
    /// null where nothing says it is. The process table is read afresh rather than from the snapshot
    /// the planning pass took, for the reason the step is asked about at all.
    /// </summary>
    private string? StillUpdating(CleanupStep step)
    {
        _inspector.Invalidate();

        if (UnfinishedUpdate.HoldsEverything(_servicing, _inspector) is { } everything)
        {
            return $"Nothing was removed: {everything}";
        }

        return step is DeleteStep delete && delete.Destroys.FirstOrDefault(_servicing.HasPendingOperationsIn) is { } pending
            ? UnfinishedUpdate.PendingNow(pending)
            : null;
    }

    /// <summary>
    /// Why <paramref name="step"/> must not run because it would destroy a folder nothing may take,
    /// or null where it would not. The last check before a removal, for a path a provider should
    /// never have planned: every provider asks <see cref="StandingFolders"/> of a folder a setting
    /// names, and this is what holds if one did not.
    /// </summary>
    private string? WhyNotTaken(DeleteStep step) =>
        step.Destroys
            .Select(path => (Path: path, Why: StandingFolders.WhyNotTaken(path, _environment, _system)))
            .FirstOrDefault(refused => refused.Why is not null) is { Why: { } why } found
                ? $"Nothing was removed: '{LongPath.Display(found.Path)}' is never removed, because {why}"
                : null;

    private async Task<StepOutcome> RunCommandAsync(RunCommandStep step, CancellationToken ct)
    {
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

        // Asked of the process table afresh rather than of the snapshot the planning pass took: the
        // user can close the program while the preview is on screen, and the tool would then start it.
        if (step.RunsOnlyWhile.Count > 0)
        {
            _inspector.Invalidate();

            if (_inspector.FindRunning(step.RunsOnlyWhile).Count == 0)
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

        if (step.TargetCheck?.WhyNot(step, ct) is { } renamed)
        {
            return new StepOutcome(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, $"Not run: {renamed}.");
        }

        // §9, looked for again on the disk immediately before the tool runs, because the tool cannot be
        // told to leave one file and a store can arrive between the preview and the clean. A folder
        // Windows will not list could hold one. See WholeTreeLook for what the look costs and why it is
        // paid here.
        var look = await WholeTreeLook.TakeAsync(step.MeasuredPaths, MinimumAge.Off, ct).ConfigureAwait(false);

        if (look.WhyNot("Not run", "The tool cannot be told to leave anything", MinimumAge.Off) is { } held)
        {
            return new StepOutcome(
                step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, held, MailStores: look.Stores.Count);
        }

        // A tool that states its own figure is asked for it immediately before the command as well as
        // after, rather than trusted from the plan. What it describes can change without Deguffer:
        // Windows cleans its component store on a schedule, and another row in the same clean can run a
        // command over the same store. The plan's figure would then credit this command with a reclaim
        // it did not make.
        var measuredBefore = step.MeasuredBy is { } asked ? await asked.MeasureAsync(ct).ConfigureAwait(false) : before;

        // Asked again, because the tool's measurement can take a minute, and an update that starts
        // during it is exactly what the hold exists for.
        if (step is { HeldWhileUpdating: true, MeasuredBy: not null } && StillUpdating(step) is { } updating)
        {
            return new StepOutcome(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, updating);
        }

        var outcome = await runner.RunAsync(step.FileName, step.Arguments, ct).ConfigureAwait(false);

        if (outcome.Succeeded && step is { Removes: { } item, Scheduled: { } scheduled })
        {
            switch (await AwaitMarkingAsync(item, scheduled, ct).ConfigureAwait(false))
            {
                case Marking.Marked:
                    return new StepOutcome(
                        step.Description,
                        Succeeded: true,
                        BytesReclaimed: 0,
                        Refusals.None,
                        $"{scheduled.Remover} removes it the next time it starts.",
                        BytesScheduled: before);

                case Marking.Neither:
                    return new StepOutcome(
                        step.Description,
                        Succeeded: false,
                        BytesReclaimed: 0,
                        Refusals.None,
                        $"{scheduled.Remover} reported success, but it neither removed {LongPath.Display(item)} "
                        + "nor marked it for removal, so nothing will happen to it.");

                case Marking.Unseen:
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

        if (step.MeasuredBy is { } tool)
        {
            measured = await tool.MeasureAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var reading = await MeasureFromDiskAsync(scanner, step.MeasuredPaths, ct).ConfigureAwait(false);

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
                + ", so nothing is counted as reclaimed)");
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

    /// <summary>
    /// What the tool did to <paramref name="item"/> in the time it was given.
    ///
    /// <para><b>Looked for rather than trusted.</b> <c>lms runtime remove</c> prints that it removed
    /// the runtime and exits zero once LM Studio has accepted the request, before LM Studio has
    /// written anything, and a failure after that point never reaches the command. The marker is the
    /// only evidence the removal will happen.</para>
    /// </summary>
    private async Task<Marking> AwaitMarkingAsync(string item, ScheduledRemoval scheduled, CancellationToken ct)
    {
        var marker = Path.Combine(item, scheduled.Marker);
        var deadline = _time.GetUtcNow() + MarkingWait;

        while (true)
        {
            if (LongPath.ProbeDirectory(item) is PathPresence.Absent)
            {
                return Marking.Removed;
            }

            switch (LongPath.ProbeFile(marker))
            {
                case PathPresence.Present:
                    return Marking.Marked;

                // Not polled further: waiting does not make Windows describe it, and reaching the
                // deadline would say the tool did nothing, which nobody saw.
                case PathPresence.Refused:
                    return Marking.Unseen;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return Marking.Neither;
            }

            await Task.Delay(MarkingPoll, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hand one volume's bin to Windows, then find out what that achieved by looking at the disk.
    ///
    /// <para><b>The reclaim is measured rather than assumed.</b> <c>SHEmptyRecycleBin</c> reports
    /// one HRESULT and no figures at all, so an estimate reported as a result would be a number
    /// nobody checked — and the estimate is a plan-time measurement of a directory anything on the
    /// machine may have written to since. Subtracting a fresh reading of the same path is the same
    /// arithmetic <see cref="RunCommandAsync"/> does after a §5.1 command, for the same reason, and
    /// it costs almost nothing here: the directory it re-measures is the one just emptied.</para>
    ///
    /// <para>The measurement comes from the disk rather than the volume snapshot, which is what
    /// makes it a second reading instead of the first one handed back. See
    /// <see cref="RunCommandAsync"/>, where that was found.</para>
    ///
    /// <para>Windows leaves the account's directory standing and empty rather than removing it,
    /// which was observed rather than assumed, so nothing here reads its absence as success.</para>
    /// </summary>
    private async Task<StepOutcome> EmptyAsync(
        EmptyRecycleBinStep step,
        MinimumAge keep,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // The guard and this route are mutually exclusive, and RecycleBinProvider is what makes
        // them so. Re-asked here because the cost of that one expression being edited wrongly is
        // the files the user explicitly asked to keep: Windows empties a bin whole and has no way
        // to hold anything back, so this step under a guard would destroy them and §5.6 would not
        // notice, since they sit inside the target rather than beside it.
        if (keep.IsOn)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: false,
                BytesReclaimed: 0,
                Refusals.None,
                "Nothing was removed: Windows cannot empty a Recycle Bin partially, and this plan "
                + "asked for recently changed files to be left alone.");
        }

        // A path that is not shaped like a bin, which nothing can currently build. Refused rather
        // than handed on, because the value that reaches the shell decides what the shell destroys.
        if (string.IsNullOrEmpty(step.VolumeRoot))
        {
            return new StepOutcome(
                step.Description,
                Succeeded: false,
                BytesReclaimed: 0,
                Refusals.None,
                "Nothing was removed: this is not the path of a Recycle Bin on a drive.");
        }

        // §9, for the reason the guard is refused above: Windows empties the bin whole, and a store
        // deleted into it since the preview, or inside a folder Windows will not list, would go with
        // everything else. Looked for on the disk, because the plan was made before it arrived.
        var look = await WholeTreeLook.TakeAsync([step.Path], MinimumAge.Off, ct).ConfigureAwait(false);

        if (look.WhyNot("Nothing was removed", "Windows empties a Recycle Bin whole", MinimumAge.Off) is { } held)
        {
            return new StepOutcome(
                step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, held, MailStores: look.Stores.Count);
        }

        // The last honest moment to stop: the call itself cannot be cancelled once it starts, and
        // on a large bin it runs for a long time. See ShellRecycleBinEmptier.
        ct.ThrowIfCancellationRequested();

        var outcome = await Task.Run(() => _emptier!.Empty(step.VolumeRoot), ct).ConfigureAwait(false);

        var after = await MeasureFromDiskAsync(scanner, [step.Path], ct).ConfigureAwait(false);

        // Nothing to report along the way — the shell offers no progress of its own, and its
        // progress window is one of the three things the flags suppress.
        progress?.Report(1.0);

        // No evidence either way, so Windows' own answer is the only thing to report and nothing is
        // credited. Taking the zero instead would report the whole bin as reclaimed.
        if (after.Unreached is not null)
        {
            return new StepOutcome(
                step.Description,
                outcome.Emptied,
                BytesReclaimed: 0,
                Refusals.None,
                $"{outcome.Message ?? "Windows reported the bin emptied."} {after.WhyNothingIsCounted}.");
        }

        var remaining = after.Size.Reclaimable;
        var reclaimed = step.EstimatedBytes - remaining;

        // The entries by the same subtraction. The bin's own folder stays standing and was never in
        // the estimate's count, so it comes out of the reading afterwards as well.
        var entriesRemoved = Math.Max(0, step.Estimated.Entries - Math.Max(0, after.Size.Entries - 1));

        // The disk is the evidence and the HRESULT is only the explanation, which is the whole
        // reason the measurement above is taken: SHEmptyRecycleBin reports S_OK and no figures, so
        // a success taken from the return value alone would be Deguffer repeating a claim it had
        // just measured to be false. A bin that still holds something, having given nothing up, is
        // a failed step whatever the shell said about it.
        var succeeded = remaining == 0 || reclaimed > 0;

        var message = (succeeded, remaining, outcome.Message) switch
        {
            (false, _, { } why) => $"{why} Everything in it is still there.",

            // The shell reported success and the bin is exactly as full as it was. Nothing else
            // here can say what went wrong, so the sentence says what is true.
            (false, _, null) =>
                $"Nothing was removed: Windows reported success and the bin still holds "
                + $"{FreeSpace.Format(remaining)}.",

            // Emptied, and it held nothing by the time the shell reached it. Distinguished from the
            // line above by the measurement rather than by the reclaim, which is zero in both.
            (true, _, _) when reclaimed <= 0 => "Emptied; it held nothing by then.",

            // Something went and something stayed: a file another process holds open is the usual
            // cause, and it is §5.3's ordinary outcome rather than a failure.
            (true, > 0, _) => $"Emptied, apart from {FreeSpace.Format(remaining)} Windows "
                + "would not release.",

            _ => "Emptied.",
        };

        return new StepOutcome(
            step.Description,
            succeeded,
            Math.Max(0, reclaimed),
            Refusals.None,
            message,
            EntriesRemoved: entriesRemoved);
    }

    private async Task<StepOutcome> DeleteAsync(
        DeleteDirectoryStep step,
        MinimumAge keep,
        RunResidue runResidue,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // A directory whose parts only mean something together is looked at first, on the disk: anything
        // the guard would keep, or a folder that would not be listed, and the removal below would leave a
        // part of it standing. A directory that goes with its index is one of these by construction. See
        // DeleteDirectoryStep.IsAllOrNothing and DeleteDirectoryStep.IndexedBy.
        //
        // §9 alone for an indivisible directory: the walk below would step over a store that arrived
        // since the preview, or one inside a folder it cannot list, and take what belongs with it. The
        // guard is not asked, because what it keeps can stay. See DeleteDirectoryStep.IsIndivisible.
        if (step.GoesWholeOrNotAtAll || step.IsIndivisible)
        {
            var (guard, how) = step.GoesWholeOrNotAtAll
                ? (keep, "Its parts only mean something together, so it goes whole or not at all")
                : (MinimumAge.Off, "What is inside it goes whole or not at all");

            var look = await WholeTreeLook.TakeAsync(step.Destroys, guard, ct).ConfigureAwait(false);

            if (look.WhyNot("Nothing was removed", how, guard) is { } partial)
            {
                return new StepOutcome(
                    step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, partial, MailStores: look.Stores.Count);
            }
        }

        // The index first, and the directory it indexes only once all of it is gone. See
        // DeleteDirectoryStep.IndexedBy.
        var index = await IndexRemoval.RemoveAsync(step, keep, refusals, runResidue, ct).ConfigureAwait(false);

        if (!index.Complete)
        {
            return index.Stopped(step);
        }

        var removal = await DirectoryRemover.RemoveAsync(step.Path, keep, progress, ct).ConfigureAwait(false);

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
        var stores = removal.MailStores.Count + index.MailStores;

        if (removal.Interrupted)
        {
            return Stopped(step, removal, stores);
        }

        var succeeded = removal.RootRemoved || removal.BytesReclaimed > 0 || removal.Kept > 0
            || removal.EntriesRemoved > 0 || stores > 0;

        var message = succeeded switch
        {
            false => LeftInPlace.WhyNothingHappened(removal.Refused, removal.RefusedFolders),

            // Nothing came out and the folder is still standing, which is the guard's own case: it
            // held back everything this step named. Saying "Removed" here would be a false statement
            // about the user's disk, and the qualifier would not rescue it — the sentence has to be
            // about what stayed, because that is all that happened.
            _ when removal is { BytesReclaimed: 0, RootRemoved: false, EntriesRemoved: 0 } && stores == 0 =>
                $"Left alone: {removal.Kept} file(s) changed too recently"
                + $"{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, kept: 0)}.",

            // The same shape with a store among what stayed, which the guard's sentence would misname.
            _ when removal is { BytesReclaimed: 0, RootRemoved: false, EntriesRemoved: 0 } =>
                $"Nothing was removed{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, mailStores: stores)}.",

            _ => $"Removed{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, mailStores: stores)}.",
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
            MailStores: stores);
    }

    /// <summary>
    /// Empty a directory and leave it standing, sparing the entries the plan named.
    ///
    /// <para>The same removal as <see cref="DeleteAsync(DeleteDirectoryStep, MinimumAge, RunResidue, IProgress{double}?, CancellationToken)"/>
    /// under different bounds, rather than a second walk of its own: §6.3's extended-length paths,
    /// §5.3's skip on a refusal, the guard on recently changed files and the refusal to follow a
    /// link are all properties of that one removal, and a parallel implementation is where one of
    /// them would go missing.</para>
    ///
    /// <para><b>Success is measured differently, because there is no root to have gone.</b> A
    /// deletion can point at the directory itself; this one cannot, so what it achieved is bytes,
    /// or the guard doing its job. A scratch folder holding nothing but live and recent files
    /// reclaims nothing and has failed at nothing.</para>
    /// </summary>
    private async Task<StepOutcome> ClearAsync(
        ClearDirectoryStep step,
        MinimumAge keep,
        RunResidue runResidue,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var removal = await DirectoryRemover.RemoveAsync(
            step.Path,
            keep,
            progress,
            ct,
            fileSystem: null,
            new RemovalBounds(KeepRoot: true, step.Spared, step.OwnedElsewhere)).ConfigureAwait(false);

        refusals.Record(step.Path, removal);
        runResidue.Record(step.Path, removal.LeftStanding);
        runResidue.RecordLeftAlone(step.Path, removal.LeftAlone);

        // A folder Windows refused is a refusal as much as a file is. Without it, a clear whose only
        // outcome was a folder a program is working in would pass as a folder that held nothing.
        var stores = removal.MailStores.Count;

        if (removal.Interrupted)
        {
            return Stopped(step, removal, stores);
        }

        var succeeded = removal.BytesReclaimed > 0 || removal.EntriesRemoved > 0 || removal.Kept > 0
            || removal.Spared > 0 || stores > 0 || (removal.Refused.IsEmpty && removal.RefusedFolders.IsEmpty);

        // Asked of the entries as well as the bytes: a folder holding only empty folders reclaims no
        // bytes, and it was still cleared.
        var message = (succeeded, removal.BytesReclaimed, removal.EntriesRemoved) switch
        {
            (false, _, _) => LeftInPlace.WhyNothingHappened(removal.Refused, removal.RefusedFolders),

            // Nothing came out, and the reasons are the ones the step promised: files too recent to
            // touch, entries something is using, and Outlook mail stores. "Cleared" would be a false
            // statement about a folder that is exactly as full as it was.
            (true, 0, 0) when removal.Kept > 0 || removal.Spared > 0 || stores > 0 =>
                "Nothing was cleared"
                + $"{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, removal.Spared, stores)}.",

            (true, 0, 0) => "It held nothing to clear.",

            _ => $"Cleared{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, removal.Spared, stores)}.",
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
            stores);
    }

    /// <summary>
    /// A removal the clean was cancelled in the middle of, with what it did before it stopped. Never a
    /// success, because the step did not do what it named.
    /// </summary>
    private static StepOutcome Stopped(CleanupStep step, RemovalOutcome removal, int stores) => new(
        step.Description,
        Succeeded: false,
        removal.BytesReclaimed,
        removal.Refused,
        (removal.BytesReclaimed == 0 && removal.EntriesRemoved == 0
            ? "Stopped when the clean was cancelled, before anything was removed"
            : "Stopped part-way when the clean was cancelled")
        + $"{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, removal.Spared, stores)}.",
        removal.Kept,
        removal.Spared,
        removal.EntriesRemoved,
        removal.RefusedFolders,
        stores,
        Interrupted: true);

    private async Task<StepOutcome> DeleteAsync(
        DeleteFileStep step,
        MinimumAge keep,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var removal = await FileRemover.RemoveAsync(step.Path, keep, ct).ConfigureAwait(false);

        // One file, so there is no fraction to report along the way — only the end of it.
        progress?.Report(1.0);

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

    /// <summary>
    /// What <paramref name="paths"/> hold now, read from the disk rather than the volume snapshot:
    /// nothing invalidates that snapshot between planning and executing, so an ordinary measurement
    /// here would hand back the figure it is about to be subtracted from.
    /// </summary>
    internal static async Task<DiskReading> MeasureFromDiskAsync(
        IDirectoryScanner scanner,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var total = ScanSize.Zero;
        string? unreached = null;

        foreach (var path in paths)
        {
            var measured = await scanner.MeasureFromDiskAsync(path, ct).ConfigureAwait(false);
            total += measured.Size;

            if (!measured.WasReached)
            {
                unreached ??= LongPath.Display(path);
            }
        }

        return new DiskReading(total, unreached);
    }

    /// <summary>What a tool that removes later was seen to do. See <see cref="AwaitMarkingAsync"/>.</summary>
    private enum Marking
    {
        Marked,
        Removed,
        Neither,

        /// <summary>Windows would not describe the marker, so whether the tool wrote it is unknown.</summary>
        Unseen,
    }
}
