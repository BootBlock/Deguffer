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

    /// <summary>
    /// Processes that, if running, mean this tool's state may be live (§5.3). Their presence is a
    /// warning on the plan, not a refusal.
    /// </summary>
    protected virtual IReadOnlyList<string> ConflictingProcessNames => [];

    public abstract Task<bool> IsPresentAsync(CancellationToken ct = default);

    public virtual void InvalidateCaches()
    {
        Environment.Invalidate();
        Inspector.Invalidate();
        Scanner.Invalidate();
    }

    public abstract Task<CleanupPlan> PlanAsync(CancellationToken ct = default);

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
    /// Measure every path a plan cares about, and produce the note that goes with them.
    ///
    /// The note is not optional: §5.5 requires the fallback to be observable, and a slow scan is
    /// otherwise indistinguishable from a large directory — the user is never told that elevating
    /// would make it quick. Bundling it with the measurement is what stops a new provider silently
    /// losing that by forgetting a separate call.
    /// </summary>
    protected async Task<ScanBatch> MeasureAllAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var sizes = new List<ScanSize>(paths.Count);
        var fallback = FallbackReason.None;

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            var measured = await Scanner.MeasureAsync(path, progress: null, ct).ConfigureAwait(false);
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
        CancellationToken ct)
    {
        var measured = await MeasureAllAsync([.. targets.Select(t => t.Path)], ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>(targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            steps.Add(new DeleteDirectoryStep(targets[i].Path, targets[i].Reason)
            {
                Estimated = measured.Sizes[i],
                LastWritten = targets[i].LastWritten,
            });
        }

        return (steps, measured);
    }

    /// <summary>
    /// The directory children of a tool root, for §5.2's recognised-child classification.
    ///
    /// A reparse point is never returned. A junction under a tool root points at a tree this
    /// provider has never classified, so deleting through it escapes the plan the user approved.
    /// An unreadable root yields nothing, which §5.3 makes the normal answer rather than an error.
    ///
    /// Shared rather than per-provider because both of those are safety facts, and the third
    /// hand-written copy is where one of them goes missing.
    /// </summary>
    protected static IReadOnlyList<DirectoryInfo> EnumerateChildDirectories(string root)
    {
        try
        {
            return
            [
                .. new DirectoryInfo(LongPath.Extended(root))
                    .EnumerateDirectories()
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint)),
            ];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }
}
