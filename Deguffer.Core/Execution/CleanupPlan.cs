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
}
