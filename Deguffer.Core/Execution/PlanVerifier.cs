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
    /// <param name="runReach">
    /// What the whole run may destroy, which is what a disappearance is measured against. Null
    /// means this plan is the whole run, which is true of a provider verified on its own.
    /// </param>
    public static VerificationResult Verify(
        CleanupPlan plan,
        RunReach? runReach = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var reach = runReach ?? RunReach.Of([plan]);
        var checks = new List<VerificationCheck>(plan.ProtectedPaths.Count);

        foreach (var protectedPath in plan.ProtectedPaths)
        {
            ct.ThrowIfCancellationRequested();
            checks.Add(Check(protectedPath, reach));
        }

        return new VerificationResult { Checks = checks };
    }

    private static VerificationCheck Check(ProtectedPath protectedPath, RunReach reach)
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

        return WasBeyondThisRunsReach(protectedPath.Path, reach)
            ? new VerificationCheck(
                protectedPath.Path,
                protectedPath.Reason,
                VerificationOutcome.RemovedFromOutside,
                "GONE — and so is the folder that held it, which no step in this run named or "
                + "deleted anything inside. Something else on the machine removed it after the "
                + "preview was made.")
            : new VerificationCheck(
                protectedPath.Path,
                protectedPath.Reason,
                VerificationOutcome.Failed,
                "MISSING — it was there before the clean.");
    }

    /// <summary>
    /// Whether a path that has gone missing went missing for a reason nothing in this run can
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
    /// <para><b>The evidence is the folder, not the path, and it takes two halves.</b> Deguffer's
    /// own deletion is confined to the tree under a step's path: <see cref="DirectoryRemover"/>
    /// removes a link rather than following it, and it never touches its own root's parent. So the
    /// folder that held a protected path went for one of two reasons. Either this run was working
    /// inside it, in which case something the run did is the first suspect and the answer is the
    /// alarming one. Or nothing in the run named that folder, named anything above it, or deleted
    /// anything inside it — and then no deletion of ours was ever near it. Only the second grants
    /// the outside reading, and it is asked of the whole run's reach rather than one plan's, because
    /// a run is many plans and another provider's deletion is not a stranger's.</para>
    ///
    /// <para>A missing path whose folder is still standing is what an over-broad rule looks like
    /// from here, so it stays a failure whatever else is true.</para>
    ///
    /// <para><b>What it cannot see.</b> The comparison is textual, and
    /// <see cref="LongPath.Extended"/> resolves no links, so a step whose <em>ancestry</em> passes
    /// through a junction deletes a physically different tree — and a protected path destroyed that
    /// way has a parent no target textually contains. That case reads as an outside removal and is
    /// not detected here. It is not the same as a link at or below a step's own root, which
    /// <see cref="DirectoryRemover"/> removes rather than descends into, and which therefore cannot
    /// destroy anything's parent at all.</para>
    ///
    /// <para>Every other branch that cannot establish the outside removal answers false, which
    /// leaves the alarming reading in place. That is the direction to fail in: a false alarm costs
    /// the user a look at the folder, and a missed one costs them the folder.</para>
    /// </summary>
    private static bool WasBeyondThisRunsReach(string path, RunReach reach)
    {
        if (reach.Unbounded || IsTargeted(path, reach.TargetedPaths))
        {
            return false;
        }

        // A volume or UNC root has nothing above it, so there is no folder whose absence could be
        // the evidence.
        //
        // The folder is not put through IsTargeted as well, and it needs no separate guard: a
        // targeted folder means a targeted path, which the line above has already answered.
        return Path.GetDirectoryName(Display(path)) is { Length: > 0 } parent
            && !LongPath.DirectoryExists(parent)
            && !HoldsATarget(parent, reach.TargetedPaths);
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
    /// Whether this run deleted anything inside <paramref name="folder"/>, which is the question
    /// <see cref="IsTargeted"/> asks the other way round.
    ///
    /// <para>It is what stops the outside reading excusing the plainest over-reach there is:
    /// execution that goes one directory higher than the plan named. A step targeting
    /// <c>Proj\obj2</c> that took <c>Proj</c> with it leaves the deselected sibling <c>Proj\obj</c>
    /// missing, its folder missing, and neither of them under any target — every condition for
    /// "something else did it", about a directory this run was working inside.</para>
    /// </summary>
    private static bool HoldsATarget(string folder, IReadOnlyList<string> targets) =>
        targets.Any(target => LongPath.Contains(Display(folder), Display(target)));

    /// <summary>
    /// A path in the one form the comparisons above are valid in: no extended-length prefix, and no
    /// trailing separator to make a folder and its own name compare unequal.
    /// </summary>
    private static string Display(string path) =>
        Path.TrimEndingDirectorySeparator(LongPath.Display(path));
}
