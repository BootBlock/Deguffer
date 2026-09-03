using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The shared shape of a provider: it supplies rules, and delegates carrying them out.
///
/// Execution lives in <see cref="PlanExecutor"/> and survival policy in <see cref="PlanVerifier"/>,
/// so a subclass contains nothing but knowledge about one cache.
/// </summary>
public abstract class CleanupProviderBase : ICleanupProvider
{
    private readonly PlanExecutor _executor;

    protected CleanupProviderBase(
        IUserEnvironment environment,
        IProcessRunner runner,
        IProcessInspector inspector,
        IDirectoryScanner scanner)
    {
        Environment = environment;
        Inspector = inspector;
        Scanner = scanner;
        _executor = new PlanExecutor(runner, scanner);
        Runner = runner;
    }

    protected IUserEnvironment Environment { get; }

    protected IProcessRunner Runner { get; }

    protected IProcessInspector Inspector { get; }

    /// <summary>
    /// How this provider learns sizes. §5.5's choice between reading the MFT and walking the tree
    /// lives entirely behind this interface: a provider states which paths it cares about and is
    /// told how big they are, and nothing here knows there are two strategies.
    /// </summary>
    protected IDirectoryScanner Scanner { get; }

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract SafetyTier Tier { get; }

    public abstract string WhatHappensOnNextUse { get; }

    public abstract ProviderDescription Description { get; }

    /// <summary>
    /// Processes that, if running, mean this tool's state may be live (§5.3). Their presence is a
    /// warning on the plan, not a refusal.
    /// </summary>
    protected virtual IReadOnlyList<string> ConflictingProcessNames => [];

    public abstract Task<bool> IsPresentAsync(CancellationToken ct = default);

    /// <summary>
    /// False for a cache provider: it knows where its own cache lives and needs no permission to
    /// look there. The providers that search the user's source trees override it.
    /// </summary>
    public virtual bool IsAwaitingSourceFolders => false;

    public virtual void InvalidateCaches()
    {
        Environment.Invalidate();
        Inspector.Invalidate();
        Scanner.Invalidate();
    }

    /// <summary>
    /// Nothing, for a provider that owns no directory whose unrecognised children must be
    /// protected. Most do not: a provider whose target <em>is</em> its cache directory has no
    /// siblings to spare, and one that finds its roots rather than knowing them cannot answer here
    /// cheaply. The providers that do own such a root override this.
    /// </summary>
    public virtual IReadOnlyList<ToolRoot> ToolRoots => [];

    /// <summary>
    /// The plan this provider builds, with the user's guard on recently touched files stamped onto
    /// it and its consequences applied.
    ///
    /// <para>Sealed here rather than left to each provider, because both halves of the guard are
    /// things a provider must not be able to forget. A plan that did not carry
    /// <see cref="CleanupPlan.Keep"/> would be executed as though no guard existed, and a provider
    /// constructs one in sixty-nine places across this project — twenty-two <c>new CleanupPlan</c>
    /// and forty-seven <see cref="EmptyPlan"/>. So it is stamped once, on whatever comes back.</para>
    /// </summary>
    public async Task<CleanupPlan> PlanAsync(MinimumAge keep = default, CancellationToken ct = default)
    {
        var plan = await BuildPlanAsync(keep, ct).ConfigureAwait(false);

        return keep.IsOn ? Guarded(plan, keep) : plan;
    }

    /// <summary>
    /// What this provider would remove. Called by <see cref="PlanAsync"/> and by nothing else.
    ///
    /// <paramref name="keep"/> is handed on to <see cref="PlanDeletionsAsync"/>, so the figures for
    /// paths Deguffer deletes itself exclude what the removal will not take. It is deliberately not
    /// handed to <see cref="MeasureAllAsync"/>, which measures a §5.1 command step's probe — see
    /// there for why that one is never guarded. A provider that measures nothing has nothing to do
    /// with it.
    /// </summary>
    protected abstract Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct);

    public Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.ProviderId != Id)
        {
            throw new ArgumentException(
                $"Plan belongs to provider '{plan.ProviderId}', not '{Id}'.", nameof(plan));
        }

        return _executor.ExecuteAsync(plan, progress, ct);
    }

    public Task<VerificationResult> VerifyAsync(CleanupPlan plan, CancellationToken ct = default) =>
        Task.FromResult(PlanVerifier.Verify(plan, ct));

    /// <summary>A plan with nothing to do, and the reason the user is shown.</summary>
    protected CleanupPlan EmptyPlan(string why) => new()
    {
        ProviderId = Id,
        ProviderName = Name,
        Tier = Tier,
        WhatHappensOnNextUse = WhatHappensOnNextUse,
        Notes = [new PlanNote(PlanNoteSeverity.Information, why)],
    };

    /// <summary>
    /// §5.6 — capture which protected paths exist now, so verification can tell "survived" from
    /// "was never there".
    /// </summary>
    protected static IReadOnlyList<ProtectedPath> Protect(params (string Path, string Reason)[] candidates) =>
    [
        .. candidates.Select(c => new ProtectedPath(
            c.Path,
            c.Reason,
            LongPath.FileExists(c.Path) || LongPath.DirectoryExists(c.Path))),
    ];

    /// <summary>§5.3 warning for this provider's processes, or null if none are running.</summary>
    protected PlanNote? BuildRunningProcessNote() =>
        RunningProcessNotice.For(Inspector, ConflictingProcessNames);

    /// <summary>
    /// Measure the paths a §5.1 command step reports against, and produce the note that goes with
    /// them.
    ///
    /// <para>The note is not optional: §5.5 requires the fallback to be observable, and a slow scan
    /// is otherwise indistinguishable from a large directory — the user is never told that elevating
    /// would make it quick. Bundling it with the measurement is what stops a new provider silently
    /// losing that by forgetting a separate call.</para>
    ///
    /// <para><b>The guard is not an argument here, and that is the point.</b> §5.1 leaves a tool's
    /// own eviction command deciding what it removes, so a figure that withheld recent files would
    /// describe a deletion nobody is going to perform. Worse, it is the figure
    /// <see cref="PlanExecutor"/> subtracts an after-measure from to report what the command
    /// reclaimed — and that after-measure comes from
    /// <see cref="IDirectoryScanner.MeasureFromDiskAsync"/>, which is unguarded for the same reason.
    /// The two sides would then be measured on different bases: the reclaim would come out short,
    /// and where the command frees less than the guard withheld it would come out negative and
    /// report that the cache grew.</para>
    ///
    /// <para>Every provider call site is a command step's probe, so refusing the argument is what
    /// makes that unmistakable. The guarded measurement is <see cref="PlanDeletionsAsync"/>'s, and
    /// it is guarded because those paths are ones Deguffer deletes itself.</para>
    /// </summary>
    protected Task<ScanBatch> MeasureAllAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        MeasureAllAsync(paths, MinimumAge.Off, ct);

    private async Task<ScanBatch> MeasureAllAsync(
        IReadOnlyList<string> paths,
        MinimumAge keep,
        CancellationToken ct)
    {
        var sizes = new List<ScanSize>(paths.Count);
        var fallback = FallbackReason.None;

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            var measured = await Scanner.MeasureAsync(path, keep, progress: null, ct).ConfigureAwait(false);
            sizes.Add(measured.Size);

            // Paths in one plan can sit on different volumes and so take different routes; the
            // first reason to appear is the one the user is shown.
            if (fallback == FallbackReason.None)
            {
                fallback = measured.Fallback;
            }
        }

        return new ScanBatch(sizes, fallback);
    }

    /// <summary>
    /// Measure every target and turn it into the step that will delete it.
    ///
    /// The pairing of a target with its size is positional, so it lives here rather than being
    /// rewritten per provider: a loop that indexes two lists in step is exactly the shape that
    /// silently attributes one directory's size to another.
    /// </summary>
    protected async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured)> PlanDeletionsAsync(
        IReadOnlyList<DeletionTarget> targets,
        MinimumAge keep,
        CancellationToken ct)
    {
        var measured = await MeasureAllAsync([.. targets.Select(t => t.Path)], keep, ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>(targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];

            DeleteStep step = target.Kind == TargetKind.File
                ? new DeleteFileStep(target.Path, target.Reason)
                : new DeleteDirectoryStep(target.Path, target.Reason);

            steps.Add(step with
            {
                Estimated = measured.Sizes[i],
                LastWritten = target.LastWritten,
                RequiresElevation = target.RequiresElevation,
            });
        }

        return (steps, measured);
    }

    /// <summary>
    /// The guard applied to a finished plan: carried onto it, said out loud, and — for a step whose
    /// whole subject is one recent file — acted on by withdrawing the offer.
    ///
    /// <para>A directory needs no withdrawing. Its recent files are already out of the estimate,
    /// and <see cref="DirectoryRemover"/> leaves them where they are, so the step stays and does
    /// less. A <see cref="DeleteFileStep"/> has nothing left to do once its one file is protected,
    /// and offering a row that will reclaim nothing is worse than not offering it — so it is
    /// withdrawn, and §5.6 is told to prove the file is still there afterwards.</para>
    /// </summary>
    private static CleanupPlan Guarded(CleanupPlan plan, MinimumAge keep)
    {
        var withdrawn = plan.Steps
            .OfType<DeleteFileStep>()
            .Where(step => keep.ProtectsFile(step.Path))
            .ToList();

        var notes = new List<PlanNote>(plan.Notes);

        // Only where there is something to say it about. Every plan comes through here, including
        // the empty one a provider returns for a toolchain that is not installed — and "the sizes
        // here already exclude those files" under "Go is not installed on this machine" describes
        // sizes that do not exist, on the majority of rows on an ordinary machine.
        if (plan.Steps.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving anything changed in the last {keep.Describe()} alone, as you asked. The "
                + "sizes here already exclude those files."));
        }

        // §5.1 keeps a tool's own eviction command as the preferred route, and that command decides
        // for itself what it removes. Saying so is the whole of what can be done about it: the
        // alternative is to stop using the command while the guard is on, which would replace the
        // tool's knowledge of its own cache with ours — the exact substitution §5.2 exists to
        // refuse. NuGet's own clear reached two locations that were not under .nuget at all.
        if (plan.Steps.OfType<RunCommandStep>().Any())
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                $"{plan.ProviderName} is cleared by running its own tool, and that tool decides what "
                + "it removes. Recent files are not protected from it."));
        }

        return plan with
        {
            Keep = keep,
            Steps = [.. plan.Steps.Except(withdrawn)],
            Notes = notes,
            ProtectedPaths =
            [
                .. plan.ProtectedPaths,
                .. withdrawn.Select(step => new ProtectedPath(
                    step.Path,
                    $"Left alone because it changed in the last {keep.Describe()}.",
                    // Measured during planning, so it was there when the plan was made — the same
                    // claim, and the same reasoning, as CleanupPlan.NarrowedTo makes for a step the
                    // user declined.
                    ExistedBefore: true)),
            ],
        };
    }
}
