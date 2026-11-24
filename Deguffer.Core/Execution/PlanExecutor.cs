using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Carries out a plan. Holds no knowledge of any cache, and none of how any kind of step is carried out:
/// it asks each step's <see cref="StepRoute"/>, and reports what happened.
/// </summary>
/// <remarks>
/// The routes for Deguffer's own work (a command, a directory, a clear and a file) are built here from
/// the collaborators every executor is given. The routes that act on the machine through Windows or a
/// sync app are given in <see cref="PlanExecutor(IProcessRunner, IDirectoryScanner, RefusalRecord, IReadOnlyList{StepRoute}?, IWindowsServicing?, IProcessInspector?, TimeProvider?, IUserEnvironment?, ISystemDirectories?, IVolumeInventory?, IFileSystem?)"/>'s
/// <c>routes</c>, and never defaulted. See <see cref="StepRoute"/>.
/// </remarks>
public sealed class PlanExecutor
{
    private readonly IDirectoryScanner _scanner;
    private readonly IProcessInspector _inspector;
    private readonly TimeProvider _time;
    private readonly UpdateHoldAtClean _updateHold;
    private readonly IUserEnvironment _environment;
    private readonly ISystemDirectories _system;
    private readonly IVolumeInventory _volumes;
    private readonly IFileSystem _fileSystem;

    /// <summary>The route of each kind of step this executor can carry out, by the step's type.</summary>
    private readonly Dictionary<Type, StepRoute> _routes;

    /// <param name="refusals">
    /// Where each removal records the places Windows refused, so the next preview of the same location
    /// can leave out what is still refused. Required rather than defaulted: the default would be the
    /// signed-in user's own record, and a caller that forgot to pass one would write into it.
    /// </param>
    /// <param name="routes">
    /// How the steps that act through Windows or a sync app are carried out: emptying a Recycle Bin, a
    /// Disk Cleanup handler, releasing cloud files, and removing restore points or profiles. Null or
    /// empty where the plans this executor runs hold none, and a plan that holds a step no route carries
    /// is then refused before any of its steps runs.
    ///
    /// <para><b>Never defaulted to the real ones.</b> Each acts on the machine of whoever runs the
    /// process, so a test that forgot to pass a fake would be one broken guard away from doing that. The
    /// real ones are given only where the app composes its providers,
    /// <see cref="CleanupPlanner.CreateDefault"/>. Two routes for one kind of step is a composition
    /// error, and is refused here.</para>
    /// </param>
    /// <param name="servicing">
    /// Asked again, with <paramref name="inspector"/>, immediately before a step that is
    /// <see cref="CleanupStep.HeldWhileUpdating"/>, and before a command that
    /// <see cref="RunCommandStep.RunsOnlyWhile"/> a program runs. Defaulted to the machine's own, because
    /// it only reads.
    /// </param>
    /// <param name="time">
    /// The clock a command waits on while its tool marks what it will remove later (see
    /// <see cref="ScheduledRemoval"/>) or Deguffer's own servicing session stops (see <see cref="UpdateHoldAtClean"/>),
    /// and the plan, each step and each removal's passes are timed on. Defaulted to the system clock, and
    /// injected so a test waits out no real interval, and so a test can say how long a step took.
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
    /// <param name="fileSystem">
    /// What the removals Deguffer performs itself list and delete through: a directory step's, a clear's
    /// and a file step's. Defaulted to the machine's own, and injected so a test can see every listing and
    /// every path a removal hands to Windows.
    /// </param>
    public PlanExecutor(
        IProcessRunner runner,
        IDirectoryScanner scanner,
        RefusalRecord refusals,
        IReadOnlyList<StepRoute>? routes = null,
        IWindowsServicing? servicing = null,
        IProcessInspector? inspector = null,
        TimeProvider? time = null,
        IUserEnvironment? environment = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null,
        IFileSystem? fileSystem = null)
    {
        _scanner = scanner;
        _inspector = inspector ?? ProcessInspector.Default;
        _time = time ?? TimeProvider.System;
        _updateHold = new UpdateHoldAtClean(servicing ?? WindowsServicing.Current, _inspector, _time);
        _environment = environment ?? UserEnvironment.Current;
        _system = system ?? SystemDirectories.Current;
        _volumes = volumes ?? VolumeInventory.Current;
        _fileSystem = fileSystem ?? WindowsFileSystem.Default;

        StepRoute[] own =
        [
            new CommandRun(runner, _inspector, _updateHold),
            new DirectoryStepRun(refusals, _fileSystem),
            new ClearRun(refusals, _fileSystem),
            new FileStepRun(refusals, _fileSystem),
        ];

        // Add rather than the indexer, so a second route for one kind of step throws here instead of
        // silently replacing the first.
        _routes = [];

        foreach (var route in own.Concat(routes ?? []))
        {
            _routes.Add(route.Carries, route);
        }

        Proof = new VerificationSources(
            [.. (routes ?? []).Select(route => route.Proof).OfType<StepProof>()], _volumes, scanner.Tuning);
    }

    /// <summary>
    /// What this executor's §5.6 verification reads the machine through: the proof of each route it was
    /// given, so a step is proved by the same object that carried it out.
    /// </summary>
    internal VerificationSources Proof { get; }

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
    /// <param name="activity">
    /// What the clean has set acting on the machine, which each step here adds to and each tool's
    /// measurement reads. See <see cref="RunActivity"/>. Null means this plan is the whole run, and it is
    /// given a record of its own.
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
        RunActivity? activity,
        IProgress<double>? progress,
        IProgress<CommandUnderway?>? underway,
        CancellationToken ct)
    {
        // Before anything runs, so a plan this executor cannot finish removes nothing rather than half
        // of itself.
        if (plan.Steps.FirstOrDefault(step => !_routes.ContainsKey(step.GetType())) is { } unroutable)
        {
            throw new InvalidOperationException(
                $"The executor for '{plan.ProviderId}' was given no way to carry out a {unroutable.GetType().Name}.");
        }

        var runResidue = residue ?? await RunResidue.BeginAsync([plan], _scanner.Tuning).ConfigureAwait(false);
        var runActivity = activity ?? new RunActivity();
        var started = _time.GetTimestamp();

        // What each step's use check held back, one list a step so the steps running at once share
        // nothing, and read in the plan's order once they have all ended.
        var heldAtClean = new List<ProtectedPath>[plan.Steps.Count];

        // One bound on the deletions of every step, so steps that run at once share the disk's queue
        // rather than each filling it. Plans run one at a time, so this is the bound for the run.
        var shared = new PlanRun(
            plan, runResidue, runActivity, new ThreadAllowance(DirectoryRemover.MostAtOnce), underway, _scanner, _time, _environment);

        // The bar is weighted as the planner weights whole plans, for the same reason: one obj
        // directory of 4 GB and five of 20 MB are six steps, and splitting the bar six ways would
        // crawl through the first sixth and then jump the rest. A step's whole weight is added when it
        // ends rather than trusted from the step: a command step reports nothing while it runs, and a
        // removal that ends early would leave a gap that never closes.
        var run = await PlanSteps.RunAsync(
            plan.Steps,
            StepOverlap.Of(plan.Steps, _volumes, _fileSystem),
            (index, stepProgress, stop) => RunTimedStepAsync(shared, index, heldAtClean[index] = [], stepProgress, stop),
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
                    Proof),
                CancellationToken.None).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Carry out the step at <paramref name="index"/>, timed from its first check to its outcome, and
    /// reported as stopped where it throws because the clean was cancelled.
    /// </summary>
    private async Task<StepOutcome> RunTimedStepAsync(
        PlanRun run,
        int index,
        List<ProtectedPath> heldAtClean,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var step = run.Plan.Steps[index];
        var clock = new StepClock(_time, _fileSystem);
        StepOutcome outcome;

        // A step that does not act from its start marks the part of itself that does. See ActsFromItsStart.
        using var acting = step.ActsFromItsStart ? run.Activity.Begin() : default;

        try
        {
            outcome = await RunStepAsync(run, step, heldAtClean, clock, progress, ct)
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
    /// Carry out one step through its route, or say why it did not run. What a use check holds back is
    /// added to <paramref name="heldAtClean"/>, because it is proved standing afterwards. The use check
    /// and any look are timed on <paramref name="clock"/>, and each deletion takes its place in the run's
    /// <see cref="PlanRun.Deletions"/>.
    /// </summary>
    private async Task<StepOutcome> RunStepAsync(
        PlanRun run,
        CleanupStep step,
        List<ProtectedPath> heldAtClean,
        StepClock clock,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (step.HeldWhileUpdating && await _updateHold.WhyHeldAsync(step, ct).ConfigureAwait(false) is { } updating)
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

                recheck = await UseRecheck.OfAsync(deletion, ct).ConfigureAwait(false);
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

        // A narrowed step is the same kind of step, so the route ExecuteAsync found for the plan's step
        // carries it too.
        return await _routes[step.GetType()].RunAsync(new StepRun(run, clock, progress), step, ct).ConfigureAwait(false);
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
}
