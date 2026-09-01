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

    /// <summary>
    /// Whether carrying this step out needs administrator rights.
    ///
    /// A different claim from <see cref="FallbackReason.NotElevated"/>, which says a *measurement*
    /// took the slow route. This one says the step can be seen and cannot be performed, and the two
    /// are independent: a location under <c>C:\Windows</c> needs elevation to remove however its
    /// size was arrived at. Both are answered by the same offer, and
    /// <see cref="ElevationOffer"/> reads both.
    ///
    /// A declaration by the provider rather than something derived from the path. Deriving it would
    /// mean a rule about which directories Windows protects, which is exactly the kind of guess §5.2
    /// refuses; declared, it is checkable by reading the provider's own table.
    ///
    /// It is a fact about the target, not about the run, so it stays true on an elevated process.
    /// Who may act on it is the shell's question, and it asks it by pairing this with the token it
    /// is actually running under.
    /// </summary>
    public bool RequiresElevation { get; init; }
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

    /// <summary>
    /// What <see cref="MeasuredPaths"/> held at plan time, where that is a different number from
    /// <see cref="CleanupStep.Estimated"/>. Null means they are the same figure, which is every
    /// provider whose estimate *is* its measurement of those paths.
    ///
    /// <para>Exists for the provider whose estimate is the tool's own accounting rather than
    /// Deguffer's: conda's dry run reports what its clean will free, while its package caches
    /// measure far larger, because everything an environment hard-links stays. Reporting the
    /// reclaim as "estimate minus what remains" would then compare two different kinds of number
    /// and call the result negative. The delta must subtract like from like, so the step carries
    /// Deguffer's own plan-time probe of the same paths the executor re-measures.</para>
    ///
    /// <para><b>It fixes the pairing, not the re-measurement.</b> The "after" figure comes from the
    /// provider's own scanner, and <see cref="Scanning.DirectoryScanner"/> holds its volume index
    /// until something invalidates it — so on an elevated run every command step, this one
    /// included, subtracts two readings of the same pre-command snapshot and reports nothing
    /// reclaimed. That defect is older and wider than this field, it belongs to the executor rather
    /// than to any provider, and closing it means deciding what rebuilding the index after each
    /// command should cost. It is recorded in <c>docs/todo/unreached-locations.md</c> §1a.</para>
    /// </summary>
    public ScanSize? MeasuredBefore { get; init; }

    public override string Description => $"{What} ({Path.GetFileName(FileName)} {Arguments})";
}

/// <summary>
/// A step that destroys one path outright.
///
/// The base exists so that "everything this plan would remove" is one question with one answer:
/// <see cref="CleanupPlan.TargetedPaths"/> and <see cref="CleanupPlan.NarrowedTo"/> both select on
/// this type, so a new kind of deletion joins the §5.2 assertions and the §5.6 negative by
/// construction rather than by somebody remembering to update two <c>OfType</c> clauses.
///
/// It is deliberately narrower than "a new kind of step". The cloud-sync dehydration in
/// <c>docs/todo/unreached-locations.md</c> §10 frees space while leaving the file present and
/// readable, so it will be a sibling of this and of <see cref="RunCommandStep"/> under
/// <see cref="CleanupStep"/> — and it must *not* appear in
/// <see cref="CleanupPlan.TargetedPaths"/>, because it destroys nothing.
/// </summary>
/// <param name="Path">The path that will be removed, in display form.</param>
/// <param name="What">Why it is disposable, written for the user.</param>
public abstract record DeleteStep(string Path, string What) : CleanupStep;

/// <summary>
/// Delete one explicitly recognised directory. Never a tool root — see <see cref="DisposableChildSet"/>.
/// </summary>
public sealed record DeleteDirectoryStep(string Path, string What) : DeleteStep(Path, What)
{
    public override string Description => $"{What} — {LongPath.Display(Path)}";
}

/// <summary>
/// Delete one explicitly named file.
///
/// Exists for <c>C:\Windows\MEMORY.DMP</c>, which is a single file and the largest single reclaim
/// Deguffer knows about — the size of installed memory after one stop error on a machine configured
/// to write a complete dump. There is no directory to target: the file's parent is the Windows
/// directory itself, which §5.2 forbids touching and this provider never enumerates.
///
/// A file is not a small tree, and the difference is a safety one rather than a convenience. A
/// directory removal walks and can partially succeed; this either removes the one path named or
/// leaves it, so <see cref="FileRemover"/> is a few lines rather than a reuse of
/// <see cref="DirectoryRemover"/> with the walking suppressed.
/// </summary>
public sealed record DeleteFileStep(string Path, string What) : DeleteStep(Path, What)
{
    public override string Description => $"{What} — {LongPath.Display(Path)}";
}
