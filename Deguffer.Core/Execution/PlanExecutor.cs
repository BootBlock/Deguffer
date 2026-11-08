using Deguffer.Core.Cloud;
using Deguffer.Core.Profiles;
using Deguffer.Core.Providers;
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
/// The clock a command waits on while its tool marks what it will remove later (see
/// <see cref="ScheduledRemoval"/>), and the one the plan, each step and each removal's passes are
/// timed on. Defaulted to the system clock, and injected so a test of a tool that never marks anything
/// does not wait out the real interval, and so a test can say how long a step took.
/// </param>
/// <param name="environment">
/// The account whose own folders no step may destroy. See <see cref="StandingFolders"/>. Defaulted
/// to the signed-in account, which is the one a provider's plan was made for.
/// </param>
/// <param name="system">The directories Windows is built out of, for the same check.</param>
/// <param name="volumes">
/// Asked every other path a step's folder is reachable at, for the same check. Defaulted to the
/// machine's own, because it only reads.
/// </param>
/// <param name="protection">
/// How a <see cref="RemoveRestorePointsStep"/> is carried out and proved. Null, and never defaulted, for
/// the reasons <paramref name="emptier"/> is: the real one removes the machine's restore points.
/// </param>
/// <param name="profiles">
/// How a <see cref="RemoveProfileStep"/> is carried out. Null, and never defaulted, for the reasons
/// <paramref name="emptier"/> is: the real one removes the machine's profiles.
/// </param>
/// <param name="fileSystem">
/// What the removals Deguffer performs itself list and delete through: a directory step's, a clear's
/// and a file step's. Defaulted to the machine's own, and injected so a test can see every listing and
/// every path a removal hands to Windows.
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
    ISystemProtection? protection = null,
    IVolumeInventory? volumes = null,
    IUserProfiles? profiles = null,
    IFileSystem? fileSystem = null)
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
    private readonly IUserProfiles? _profiles = profiles;
    private readonly IWindowsServicing _servicing = servicing ?? WindowsServicing.Current;
    private readonly IProcessInspector _inspector = inspector ?? ProcessInspector.Default;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IUserEnvironment _environment = environment ?? UserEnvironment.Current;
    private readonly ISystemDirectories _system = system ?? SystemDirectories.Current;
    private readonly IVolumeInventory _volumes = volumes ?? VolumeInventory.Current;
    private readonly IFileSystem _fileSystem = fileSystem ?? WindowsFileSystem.Default;

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
    /// <param name="underway">
    /// Each command step's tool as it starts, and null as it returns, so the page can say what a
    /// cancellation would wait for. See <see cref="CommandUnderway"/>.
    /// </param>
    /// <param name="ct">
    /// Stops new steps from starting, and each step under way that can stop. The steps run several at
    /// once where they are apart (see <see cref="PlanSteps"/>). <b>Never thrown:</b> the result
    /// holds the steps that ran, is <see cref="CleanupResult.Interrupted"/>, and is verified like any
    /// other, because what a cancelled clean already took is what §5.6 has to answer for.
    /// </param>
    public async Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        RunReach? runReach,
        RunResidue? residue,
        IProgress<double>? progress,
        IProgress<CommandUnderway?>? underway,
        CancellationToken ct)
    {
        // Before anything runs, so a plan this executor cannot finish removes nothing rather than half
        // of itself.
        if (plan.Steps.FirstOrDefault(step => !HasRouteFor(step)) is { } unroutable)
        {
            throw new InvalidOperationException(
                $"The executor for '{plan.ProviderId}' was given no way to carry out a {unroutable.GetType().Name}.");
        }

        var runResidue = residue ?? RunResidue.Begin([plan]);
        var started = _time.GetTimestamp();

        // What each step's use check held back, one list a step so the steps running at once share
        // nothing, and read in the plan's order once they have all ended.
        var heldAtClean = new List<ProtectedPath>[plan.Steps.Count];

        // One bound on the deletions of every step, so steps that run at once share the disk's queue
        // rather than each filling it. Plans run one at a time, so this is the bound for the run.
        var deletions = new ThreadAllowance(DirectoryRemover.MostAtOnce);

        // The bar is weighted as the planner weights whole plans, for the same reason: one obj
        // directory of 4 GB and five of 20 MB are six steps, and splitting the bar six ways would
        // crawl through the first sixth and then jump the rest. A step's whole weight is added when it
        // ends rather than trusted from the step: a command step reports nothing while it runs, and a
        // removal that ends early would leave a gap that never closes.
        var run = await PlanSteps.RunAsync(
            plan.Steps,
            StepOverlap.Of(plan.Steps, _volumes, _fileSystem),
            (index, stepProgress, stop) => RunTimedStepAsync(
                plan, index, heldAtClean[index] = [], runResidue, deletions, stepProgress, underway, stop),
            progress,
            ct).ConfigureAwait(false);

        var duration = _time.GetElapsedTime(started);
        var held = heldAtClean.Take(run.Outcomes.Count).SelectMany(step => step).ToList();

        return new CleanupResult
        {
            ProviderId = plan.ProviderId,
            ProviderName = plan.ProviderName,
            Steps = run.Outcomes,
            Duration = duration,
            Interrupted = run.Interrupted,

            // §5.6 is not a separate user action: acting and proving what survived are one step. What a
            // use check held back is proved standing with everything the plan protected.
            //
            // Never cancelled, because a cancelled clean is the one that most needs it: something has
            // gone, and the user chose where it stopped without knowing what it had reached.
            //
            // On a thread of the proof's own, never the pool's: it probes the disk for every survivor
            // and drives the reads of a release step's files. See ListWorkers.
            Verification = await WorkerThreads.Shared.RunAsync(
                () => PlanVerifier.Verify(
                    held.Count == 0 ? plan : plan with { ProtectedPaths = [.. plan.ProtectedPaths, .. held] },
                    runReach,
                    runResidue,
                    CancellationToken.None,
                    _cloud,
                    _protection,
                    _volumes,
                    _profiles),
                CancellationToken.None).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Carry out the step at <paramref name="index"/>, timed from its first check to its outcome, and
    /// reported as stopped where it throws because the clean was cancelled.
    /// </summary>
    private async Task<StepOutcome> RunTimedStepAsync(
        CleanupPlan plan,
        int index,
        List<ProtectedPath> heldAtClean,
        RunResidue runResidue,
        ThreadAllowance deletions,
        IProgress<double>? progress,
        IProgress<CommandUnderway?>? underway,
        CancellationToken ct)
    {
        var step = plan.Steps[index];
        var clock = new StepClock(_time);
        StepOutcome outcome;

        try
        {
            outcome = await RunStepAsync(plan, step, heldAtClean, runResidue, deletions, clock, progress, underway, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A step that throws when cancelled cannot say what it did first. A tool's command can
            // have removed part of its cache, and nothing counted it.
            outcome = new StepOutcome(
                step.Description,
                Succeeded: false,
                BytesReclaimed: 0,
                Refusals.None,
                "Stopped when the clean was cancelled. What it had removed by then is not counted.",
                Interrupted: true);
        }

        return outcome with { Timing = clock.Read() };
    }

    /// <summary>
    /// Carry out one step, or say why it did not run. What a use check holds back is added to
    /// <paramref name="heldAtClean"/>, because it is proved standing afterwards. The use check and any
    /// look are timed on <paramref name="clock"/>, and each deletion takes its place in
    /// <paramref name="deletions"/>.
    /// </summary>
    private async Task<StepOutcome> RunStepAsync(
        CleanupPlan plan,
        CleanupStep step,
        List<ProtectedPath> heldAtClean,
        RunResidue runResidue,
        ThreadAllowance deletions,
        StepClock clock,
        IProgress<double>? progress,
        IProgress<CommandUnderway?>? underway,
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

        // §5.3 asked again: the plan's answer about what is in use is as old as the preview. A check that
        // can ask its programs to let go does so first, and is then asked like any other.
        if (step is DeleteStep deletion)
        {
            var asked = clock.Mark();
            UseRecheck recheck;

            // Timed in a finally, because a release cancelled while it waits for a program to exit
            // throws, and that wait is the time a cancelled step most needs to account for.
            try
            {
                if (deletion.UseCheck is ReleasingProcessCheck releasing)
                {
                    await releasing.ReleaseAsync(ct).ConfigureAwait(false);
                }

                recheck = UseRecheck.Of(deletion, ct);
            }
            finally
            {
                clock.UseCheckSince(asked);
            }

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
            RunCommandStep command => await RunCommandAsync(command, clock, underway, ct).ConfigureAwait(false),
            ClearDirectoryStep clear => await ClearAsync(clear, plan.Keep, runResidue, deletions, progress, ct).ConfigureAwait(false),
            DeleteDirectoryStep delete => await DeleteAsync(delete, plan.Keep, runResidue, deletions, progress, ct).ConfigureAwait(false),
            DeleteFileStep delete => await DeleteAsync(delete, plan.Keep, deletions, progress, ct).ConfigureAwait(false),
            EmptyRecycleBinStep empty => await EmptyAsync(empty, plan.Keep, clock, progress, ct).ConfigureAwait(false),
            DiskCleanupStep handler => await DiskCleanupRun.RunAsync(_handlers!, scanner, handler, plan.Keep, clock, progress, ct).ConfigureAwait(false),
            ReleaseLocalCopiesStep release => await LocalCopyRelease.RunAsync(_cloud!, release, plan.Keep, progress, ct).ConfigureAwait(false),
            RemoveRestorePointsStep removal => await RestorePointRun.RunAsync(_protection!, removal, progress, ct).ConfigureAwait(false),
            RemoveProfileStep profile => await ProfileRemovalRun.RunAsync(_profiles!, _environment, scanner, profile, clock, ct).ConfigureAwait(false),
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
        RemoveProfileStep => _profiles is not null,
        _ => true,
    };

    /// <summary>
    /// Why <paramref name="step"/> must not run because Windows is now in the middle of an update, or
    /// null where nothing says it is. The process table is read again where the snapshot is past
    /// <see cref="IUseCheck.ReadingLimit"/>, for the reason the step is asked about at all.
    /// </summary>
    private string? StillUpdating(CleanupStep step)
    {
        _inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

        if (UnfinishedUpdate.HoldsEverything(_servicing, _inspector, (step as RunCommandStep)?.ServicingCalls) is { } everything)
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
    ///
    /// <para><b>Asked of the machine at the removal, never from a remembered answer</b> such as
    /// <see cref="RememberedMountPoints"/> gives a pass. A remembered answer is safe where both sides
    /// of a comparison are followed, and here only the destroyed path is: the account's folders are
    /// compared as Windows names them. A step destroys one path or a handful, so remembering would
    /// save almost nothing, and this is the check that holds when a provider's remembered answer
    /// missed a mount made since.</para>
    /// </summary>
    private string? WhyNotTaken(DeleteStep step) =>
        step.Destroys
            .Select(path => (Path: path, Why: StandingFolders.WhyNotTaken(path, _environment, _system, _volumes)))
            .FirstOrDefault(refused => refused.Why is not null) is { Why: { } why } found
                ? $"Nothing was removed: '{LongPath.Display(found.Path)}' is never removed, because {why}"
                : null;

    private async Task<StepOutcome> RunCommandAsync(
        RunCommandStep step,
        StepClock clock,
        IProgress<CommandUnderway?>? underway,
        CancellationToken ct)
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

        // Asked of a process table within the use checks' limit rather than of the snapshot the
        // planning pass took: the user can close the program while the preview is on screen, and the
        // tool would then start it.
        if (step.RunsOnlyWhile.Count > 0)
        {
            _inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

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

        // Asked before the look so a command already refused costs no walk, and again once the look
        // and any measurement are done.
        if (step.TargetCheck?.WhyNot(step, ct) is { } retargeted)
        {
            return Retargeted(step, retargeted);
        }

        // §9, looked for again on the disk immediately before the tool runs, because the tool cannot be
        // told to leave one file and a store can arrive between the preview and the clean. A folder
        // Windows will not list could hold one. See WholeTreeLook for what the look costs and why it is
        // paid here.
        var look = await clock.LookAsync(step.MeasuredPaths, MinimumAge.Off, ct).ConfigureAwait(false);

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
            ? await asked.MeasureAsync(MeasuredWhen.BeforeCommand, ct).ConfigureAwait(false)
            : before;

        // Asked again, because the tool's measurement can take a minute, and an update that starts
        // during it is exactly what the hold exists for. The servicing stack the measurement itself
        // woke is not that update, where the step names the record it was made in: see
        // RunCommandStep.ServicingCalls.
        if (step is { HeldWhileUpdating: true, MeasuredBy: not null } && StillUpdating(step) is { } updating)
        {
            return new StepOutcome(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, updating);
        }

        // Asked again, because the look walks every path the command clears and the tool's measurement
        // can take a minute, and what a target check guards against happens at the machine's pace, not
        // the run's: a build started, or a handle given to another item, while either ran.
        if (step.TargetCheck?.WhyNot(step, ct) is { } retargetedSince)
        {
            return Retargeted(step, retargetedSince);
        }

        var run = ToolRun.Command(step.WhenCancelled).Without(step.Unset);

        // Around the tool alone, rather than the whole step: the look and the measurement above stop
        // when the clean is cancelled, and only the tool can hold the run past a press of Cancel.
        underway?.Report(CommandUnderway.Of(step));

        CommandOutcome outcome;

        try
        {
            outcome = step.ServicingCalls is { } calls
                ? await calls.MakeAsync(() => runner.RunAsync(step.FileName, step.Arguments, run, ct)).ConfigureAwait(false)
                : await runner.RunAsync(step.FileName, step.Arguments, run, ct).ConfigureAwait(false);
        }
        finally
        {
            underway?.Report(null);
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
            var when = outcome.Succeeded ? MeasuredWhen.AfterCommandSucceeded : MeasuredWhen.AfterCommandFailed;
            measured = await tool.MeasureAsync(when, ct).ConfigureAwait(false);
        }
        else
        {
            var reading = await MeasureFromDiskAsync(scanner, step.MeasuredPaths, default, ct).ConfigureAwait(false);

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
            // The entry probe, because the directory probe answers Absent for a file at the path too.
            if (LongPath.ProbeEntry(item) is PathPresence.Absent)
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
        StepClock clock,
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
        var look = await clock.LookAsync([step.Path], MinimumAge.Off, ct).ConfigureAwait(false);

        if (look.WhyNot("Nothing was removed", "Windows empties a Recycle Bin whole", MinimumAge.Off) is { } held)
        {
            return new StepOutcome(
                step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, held, MailStores: look.Stores.Count);
        }

        // The last honest moment to stop: the call itself cannot be cancelled once it starts, and
        // on a large bin it runs for a long time. See ShellRecycleBinEmptier.
        ct.ThrowIfCancellationRequested();

        var outcome = await Task.Run(() => _emptier!.Empty(step.VolumeRoot), ct).ConfigureAwait(false);

        var after = await MeasureFromDiskAsync(scanner, [step.Path], ScanBounds.Contents, ct).ConfigureAwait(false);

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

        // The entries by the same subtraction. The bin's own folder stays standing and is in neither
        // count, because both readings keep it.
        var entriesRemoved = Math.Max(0, step.Estimated.Entries - after.Size.Entries);

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
        ThreadAllowance deletions,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (WholeDirectoryRemoval.Applies(step))
        {
            return await WholeDirectoryRemoval.RemoveAsync(
                    step, keep, refusals, runResidue, progress, _fileSystem, _time, deletions, ct)
                .ConfigureAwait(false);
        }

        var removal = await DirectoryRemover.RemoveAsync(
                step.Path, keep, progress, ct, _fileSystem, time: _time, plannedEntries: step.Estimated.Entries, deletions: deletions)
            .ConfigureAwait(false);

        refusals.Record(step.Path, removal);
        runResidue.Record(step.Path, removal.LeftStanding);

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
                $"Left alone: {removal.Kept} file(s) changed too recently"
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
    /// Empty a directory and leave it standing, sparing the entries the plan named.
    ///
    /// <para>The same removal as <see cref="DeleteAsync(DeleteDirectoryStep, MinimumAge, RunResidue, ThreadAllowance, IProgress{double}?, CancellationToken)"/>
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
        ThreadAllowance deletions,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var removal = await DirectoryRemover.RemoveAsync(
            step.Path,
            keep,
            progress,
            ct,
            _fileSystem,
            new RemovalBounds(KeepRoot: true, step.Spared, step.OwnedElsewhere, step.UserWork),
            _time,
            step.Estimated.Entries,
            deletions).ConfigureAwait(false);

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

    /// <summary>A command not run because its <see cref="RunCommandStep.TargetCheck"/> said why not.</summary>
    private static StepOutcome Retargeted(RunCommandStep step, string why) =>
        new(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, $"Not run: {why}.");

    /// <summary>
    /// A removal the clean was cancelled in the middle of, with what it did before it stopped. Never a
    /// success, because the step did not do what it named.
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

    private async Task<StepOutcome> DeleteAsync(
        DeleteFileStep step,
        MinimumAge keep,
        ThreadAllowance deletions,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var removal = await FileRemover.RemoveAsync(step.Path, keep, ct, _fileSystem, deletions).ConfigureAwait(false);

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
    /// here would hand back the figure it is about to be subtracted from. All of them in one reading,
    /// so a step naming several paths on one kind of drive pays for one walk rather than one a path.
    /// </summary>
    /// <param name="bounds">What each reading leaves standing. See <see cref="ScanBounds"/>.</param>
    internal static async Task<DiskReading> MeasureFromDiskAsync(
        IDirectoryScanner scanner,
        IReadOnlyList<string> paths,
        ScanBounds bounds,
        CancellationToken ct)
    {
        var measured = await scanner
            .MeasureFromDiskAsync([.. paths.Select(path => new ScanRequest(path, bounds))], ct)
            .ConfigureAwait(false);

        var total = ScanSize.Zero;
        string? unreached = null;

        for (var i = 0; i < paths.Count; i++)
        {
            total += measured[i].Size;

            if (!measured[i].WasReached)
            {
                unreached ??= LongPath.Display(paths[i]);
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
