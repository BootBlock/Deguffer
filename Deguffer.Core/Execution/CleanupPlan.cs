using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>One action in a plan. Nothing here has been executed.</summary>
public abstract record CleanupStep
{
    /// <summary>What the user is told this step will do.</summary>
    public abstract string Description { get; }

    /// <summary>
    /// What this step is expected to reclaim, measured at plan time.
    ///
    /// A <see cref="ScanSize"/> rather than a bare count because allocated and logical bytes are
    /// legitimately different numbers on compressed and sparse trees, and because this is where
    /// §5.4's second pair — reclaimed inside a virtual disk versus on the host — will belong when a
    /// container provider arrives.
    /// </summary>
    public ScanSize Estimated { get; init; }

    /// <summary>The single number to show and to subtract: what the volume actually gives back.</summary>
    public long EstimatedBytes => Estimated.Reclaimable;

    /// <summary>
    /// When this step's subject was last written, or null where the provider cannot tell.
    ///
    /// §7 makes age a first-class column for per-workspace and per-project data, on the grounds
    /// that "last touched 5 months ago" drives the decision more than size does. Null is a real
    /// answer and must stay distinguishable from an old one — <see cref="RelativeAge"/> renders it
    /// as unknown, never as an age, because an age is what invites the user to delete something.
    ///
    /// Whole-cache steps leave this null: a single timestamp across a tool's entire cache would be
    /// a number with no meaning attached to it.
    /// </summary>
    public DateTime? LastWritten { get; init; }
}

/// <summary>
/// Invoke a tool's own eviction command (§5.1) — always preferred over deleting paths, because
/// the tool knows about locations we do not.
/// </summary>
public sealed record RunCommandStep(string FileName, string Arguments, string What) : CleanupStep
{
    /// <summary>
    /// The locations we expect the command to clear. Used only to measure what it actually
    /// reclaimed — the command remains the authority on *what* gets removed, which is the whole
    /// point of §5.1. NuGet's own clear reached two locations that were not under <c>.nuget</c>
    /// at all, so this list is a probe, never a target.
    /// </summary>
    public IReadOnlyList<string> MeasuredPaths { get; init; } = [];

    public override string Description => $"{What} ({Path.GetFileName(FileName)} {Arguments})";
}

/// <summary>
/// Delete one explicitly recognised directory. Never a tool root — see <see cref="DisposableChildSet"/>.
/// </summary>
public sealed record DeleteDirectoryStep(string Path, string What) : CleanupStep
{
    public override string Description => $"{What} — {LongPath.Display(Path)}";
}

/// <summary>
/// A path the plan asserts will still be there afterwards (§5.6). Verifying the negative is
/// cheap, and it catches an over-broad rule on the first run rather than the hundredth.
/// </summary>
/// <param name="Path">The path that must survive.</param>
/// <param name="Reason">Why it matters — shown to the user in the verification report.</param>
/// <param name="ExistedBefore">
/// Whether it was present when the plan was made. A path that was never there cannot have been
/// destroyed, so only the ones that existed constitute evidence.
/// </param>
public sealed record ProtectedPath(string Path, string Reason, bool ExistedBefore);

/// <summary>A remark attached to a plan: something the user should know before confirming.</summary>
public sealed record PlanNote(PlanNoteSeverity Severity, string Message);

public enum PlanNoteSeverity
{
    Information,
    Warning,
}

/// <summary>
/// Exactly what would happen, computed but never executed. §7: the dry run is the default
/// action, so this is the object the primary button produces.
/// </summary>
public sealed record CleanupPlan
{
    public required string ProviderId { get; init; }

    /// <summary>The named cause, not the path — "Gradle build cache" (§2).</summary>
    public required string ProviderName { get; init; }

    public required SafetyTier Tier { get; init; }

    /// <summary>§7: every row states what happens on next use.</summary>
    public required string WhatHappensOnNextUse { get; init; }

    public IReadOnlyList<CleanupStep> Steps { get; init; } = [];

    public IReadOnlyList<ProtectedPath> ProtectedPaths { get; init; } = [];

    public IReadOnlyList<PlanNote> Notes { get; init; } = [];

    /// <summary>
    /// Which route measured this plan's paths. <see cref="FallbackReason.None"/> for a plan with
    /// nothing to measure, which is correct: an empty plan gives the user no reason to elevate.
    ///
    /// The matching sentence is already in <see cref="Notes"/>; this is the same fact in a form the
    /// UI can act on, because "would elevating help here?" is a decision, not a sentence.
    /// </summary>
    public FallbackReason Fallback { get; init; } = FallbackReason.None;

    /// <summary>Total reclaim estimated across all steps.</summary>
    public long EstimatedBytes => Steps.Sum(s => s.EstimatedBytes);

    /// <summary>
    /// The same total with both numbers intact, and with the approximation flag preserved: a plan
    /// measured wholly or partly by the fallback walk cannot claim exact allocated sizes.
    /// </summary>
    public ScanSize Estimated => Steps.Aggregate(ScanSize.Zero, (total, step) => total + step.Estimated);

    /// <summary>A plan with no steps is a no-op — the toolchain is absent, or already clean.</summary>
    public bool IsEmpty => Steps.Count == 0;

    /// <summary>
    /// Every directory this plan would delete, for display and for tests.
    ///
    /// Deliberately not cached in a backing field: this is a record, and a <c>with</c> expression
    /// copies backing fields wholesale, so a cached list would survive a change to
    /// <see cref="Steps"/> and quietly describe the wrong plan. This is the collection the safety
    /// tests assert against, which makes it the last place a stale value is acceptable. Steps
    /// number in the low single digits, so recomputing costs nothing.
    /// </summary>
    public IReadOnlyList<string> TargetedPaths =>
        [.. Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path)];

    /// <summary>
    /// This plan narrowed to the steps the user actually chose.
    ///
    /// Narrowing lives here rather than in the shell because of what it has to do besides drop
    /// steps: every deletion the user declined becomes a protected path. §5.6's negative is the
    /// promise that a step which did not run left its subject standing, and after per-item
    /// selection the deselected directory is a sibling of the selected one — same parent, same
    /// shape — which is exactly when an over-broad rule takes both. A shell that narrowed a plan by
    /// filtering <see cref="Steps"/> itself would silently drop that guarantee, so the only
    /// narrowing available adds it.
    ///
    /// A dropped <see cref="RunCommandStep"/> contributes no protection: its
    /// <see cref="RunCommandStep.MeasuredPaths"/> are a probe rather than a target (§5.1), and
    /// asserting the tool left them alone would be asserting something this plan never controlled.
    /// </summary>
    public CleanupPlan NarrowedTo(IReadOnlyCollection<CleanupStep> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);

        var kept = Steps.Where(keep.Contains).ToList();

        if (kept.Count == Steps.Count)
        {
            return this;
        }

        var declined = Steps
            .Except(kept)
            .OfType<DeleteDirectoryStep>()
            .Select(s => new ProtectedPath(
                s.Path,
                "Left alone because it was not selected for this run.",
                // It was measured during planning, so it was there when the plan was made. That is
                // the only claim ExistedBefore makes, and re-probing the disk here would let a
                // directory deleted between planning and execution excuse itself.
                ExistedBefore: true));

        return this with
        {
            Steps = kept,
            ProtectedPaths = [.. ProtectedPaths, .. declined],
        };
    }
}
