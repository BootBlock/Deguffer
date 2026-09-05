using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// §5.6 — assert that the things that should have survived did.
///
/// This is policy, not mechanism: what counts as evidence of survival is a safety decision, and
/// it lives in one place so it can be read and changed without touching execution.
/// </summary>
public static class PlanVerifier
{
    public static VerificationResult Verify(CleanupPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var targets = plan.TargetedPaths;

        // §5.1 leaves a tool's own eviction command deciding what it removes, so a plan holding one
        // has no bounded reach to measure a disappearance against. Every missing path in such a plan
        // stays this run's to answer for.
        var unbounded = plan.Steps.OfType<RunCommandStep>().Any();

        var checks = new List<VerificationCheck>(plan.ProtectedPaths.Count);

        foreach (var protectedPath in plan.ProtectedPaths)
        {
            ct.ThrowIfCancellationRequested();
            checks.Add(Check(protectedPath, targets, unbounded));
        }

        return new VerificationResult { Checks = checks };
    }

    private static VerificationCheck Check(
        ProtectedPath protectedPath,
        IReadOnlyList<string> targets,
        bool unbounded)
    {
        // A path that was never there cannot be evidence of survival. Recording it with an honest
        // detail keeps the report from overstating what the run actually established.
        if (!protectedPath.ExistedBefore)
        {
            return new VerificationCheck(
                protectedPath.Path,
                protectedPath.Reason,
                VerificationOutcome.NotPresentBefore,
                "Not present before the clean; nothing to preserve.");
        }

        if (LongPath.FileExists(protectedPath.Path) || LongPath.DirectoryExists(protectedPath.Path))
        {
            return new VerificationCheck(
                protectedPath.Path, protectedPath.Reason, VerificationOutcome.Survived, "Still present.");
        }

        return WasBeyondThisRunsReach(protectedPath.Path, targets, unbounded)
            ? new VerificationCheck(
                protectedPath.Path,
                protectedPath.Reason,
                VerificationOutcome.RemovedFromOutside,
                "GONE — and so is the folder that held it, which no step in this run named. Something "
                + "else on the machine removed it after the preview was made.")
            : new VerificationCheck(
                protectedPath.Path,
                protectedPath.Reason,
                VerificationOutcome.Failed,
                "MISSING — it was there before the clean.");
    }

    /// <summary>
    /// Whether a path that has gone missing went missing for a reason nothing in this plan can
    /// account for.
    ///
    /// <para><b>Why the question is worth asking at all.</b> A plan is built when the user presses
    /// Preview and carried out when they press Clean, and <see cref="ProtectedPath.ExistedBefore"/>
    /// is a claim about the first of those instants. Anything at all may happen to the disk in
    /// between — on a machine where build directories are the subject, a removed source checkout is
    /// an ordinary event. Reading every such disappearance as an over-broad rule tells the user to
    /// report a fault that is not there, and a §5.6 alarm that cries wolf is worth less than no
    /// alarm.</para>
    ///
    /// <para><b>The evidence is the folder, not the path.</b> Deguffer's own deletion is confined to
    /// the tree under a step's path: <see cref="DirectoryRemover"/> removes a link rather than
    /// following it, and it never touches its own root's parent. So it cannot remove the folder that
    /// held a protected path unless the plan named that folder or something above it. A missing path
    /// whose <em>parent folder is missing too</em> was therefore taken by something else — while a
    /// missing path in a folder still standing is exactly what an over-broad rule looks like from
    /// here, including the kind that escapes a tree it was meant to stay inside.</para>
    ///
    /// <para>Every branch that cannot establish the outside removal answers false, which leaves the
    /// alarming reading in place. That is the direction to fail in: a false alarm costs the user a
    /// look at the folder, and a missed one costs them the folder.</para>
    /// </summary>
    private static bool WasBeyondThisRunsReach(string path, IReadOnlyList<string> targets, bool unbounded)
    {
        if (unbounded || IsTargeted(path, targets))
        {
            return false;
        }

        // A volume or UNC root has nothing above it, so there is no folder whose absence could be
        // the evidence.
        //
        // The folder is not put through IsTargeted as well, and it needs no separate guard: a
        // targeted folder means a targeted path, which the line above has already answered.
        return Path.GetDirectoryName(Display(path)) is { Length: > 0 } parent
            && !LongPath.DirectoryExists(parent);
    }

    /// <summary>
    /// Whether this run's own deletion could have reached <paramref name="path"/>.
    ///
    /// A step's path may carry the extended-length prefix (§6.3) where a protected path does not, so
    /// both sides are put into display form rather than compared as they arrive. A prefix on one
    /// side alone would make a containment test answer no about a path the run deleted outright.
    /// </summary>
    private static bool IsTargeted(string path, IReadOnlyList<string> targets) =>
        targets.Any(target => LongPath.Contains(Display(target), Display(path)));

    /// <summary>
    /// A path in the one form the comparisons above are valid in: no extended-length prefix, and no
    /// trailing separator to make a folder and its own name compare unequal.
    /// </summary>
    private static string Display(string path) =>
        Path.TrimEndingDirectorySeparator(LongPath.Display(path));
}
