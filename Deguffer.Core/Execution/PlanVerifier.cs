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
    /// <param name="residue">
    /// What the run's removals have left standing so far. Null means nothing was removed, which is
    /// true of a verification with no execution behind it.
    /// </param>
    public static VerificationResult Verify(
        CleanupPlan plan,
        RunReach? runReach = null,
        RunResidue? residue = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var reach = runReach ?? RunReach.Of([plan]);
        var checks = new List<VerificationCheck>(plan.ProtectedPaths.Count);

        foreach (var protectedPath in plan.ProtectedPaths)
        {
            ct.ThrowIfCancellationRequested();
            checks.Add(Check(protectedPath, reach, residue));
        }

        return new VerificationResult { Checks = checks };
    }

    private static VerificationCheck Check(ProtectedPath protectedPath, RunReach reach, RunResidue? residue)
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
            // Asked before the emptied question, because it is the more exact of the two. It names
            // Deguffer's own removal as the one that went inside, where an emptied folder may still be
            // a tool's doing in a run that holds a command. And it does not depend on what the folder
            // still holds, which is the thing a refusal hides.
            if (residue?.Entered(protectedPath.Path) == true)
            {
                return new VerificationCheck(
                    protectedPath.Path,
                    protectedPath.Reason,
                    VerificationOutcome.Entered,
                    "ENTERED — the folder is still here, but a removal in this run went inside it and "
                    + "could not take everything it tried to.");
            }

            return WasEmptied(protectedPath, reach)
                ? new VerificationCheck(
                    protectedPath.Path,
                    protectedPath.Reason,
                    VerificationOutcome.Emptied,
                    "EMPTIED — the folder is still here and everything that was in it has gone. "
                    + "No step in this run named anything inside it.")
                : new VerificationCheck(
                    protectedPath.Path, protectedPath.Reason, VerificationOutcome.Survived, "Still present.");
        }

        return WasBeyondThisRunsReach(protectedPath.Path, reach)
            ? new VerificationCheck(
                protectedPath.Path,
                protectedPath.Reason,
                VerificationOutcome.RemovedFromOutside,
                "GONE — and so is the folder that held it, which no step in this run named or "
                + "deleted anything inside. Something else on the machine removed it after the "
                + "scan was made.")
            : new VerificationCheck(
                protectedPath.Path,
                protectedPath.Reason,
                VerificationOutcome.Failed,
                "MISSING — it was there before the clean.");
    }

    /// <summary>
    /// Whether a protected directory that held something now holds nothing, for a reason this run
    /// cannot account for.
    ///
    /// <para><b>Why existence alone stopped being the whole question.</b> Until a route existed that
    /// empties a directory in place, an over-broad rule always took the directory with the contents,
    /// so a protected sibling went missing and the check above caught it. It no longer does.
    /// <see cref="EmptyRecycleBinStep"/> hands Windows a whole volume and Windows empties one
    /// account's bin inside it, and the failure worth catching — it reached every account — leaves
    /// each of those directories exactly where it was, holding nothing. Every protected path is then
    /// present, the negative passes, and what it passed over is another person's deleted files.
    /// See <see cref="ProtectedPath.HeldContentBefore"/>.</para>
    ///
    /// <para><b>What the run named is the exemption, and it is the reasoning
    /// <see cref="HoldsAnyOf"/> already carries.</b> A protected path is often the parent of a
    /// target — a volume's <c>$Recycle.Bin</c> is protected and this account's bin inside it is
    /// removed — so on a machine with one account that parent legitimately ends the run empty.
    /// Reading that as an alarm would fire on the ordinary case and teach the reader to ignore it.
    /// A directory holding nothing this run named, that held something before and holds nothing now,
    /// has no such explanation.</para>
    ///
    /// <para><b>A tool's own command names where it is sent.</b> Its
    /// <see cref="RunCommandStep.MeasuredPaths"/> are the plan's statement of what the tool will
    /// clear, so a tool root holding its cache can end the run empty for the reason above, and so can
    /// the cache itself. See <see cref="RunReach.ProbedPaths"/>.</para>
    ///
    /// <para><b>An unbounded reach is not an exemption.</b> §5.1 gives a command a reach nobody can
    /// state, and that makes an emptied folder the run's to answer for exactly as it makes a missing
    /// one. Reading it as a reason to excuse every emptied folder would switch this check off on the
    /// runs where Deguffer controls least, and a File History drive holding other accounts' backups
    /// is one of them.</para>
    ///
    /// <para><b>How far the question looks depends on what ran.</b> A tool's own command empties
    /// files in place and can leave every folder that held them standing, so in a run holding one the
    /// question is whether content is left anywhere below. In a run without one, the question is
    /// whether anything at all is left. A folder holding only empty folders is there most often
    /// something else's doing: MSBuild's Clean, run while the preview sat on screen, leaves exactly
    /// that in the <c>bin</c> beside every <c>obj</c> this run removes, and an alarm about it would cry
    /// wolf about a folder Deguffer never touched.</para>
    ///
    /// <para><b>What that costs, and what answers it instead.</b> Deguffer's own removal can leave the
    /// same shape. Windows refuses to remove a directory a process is working in, so an over-reach
    /// that meets one leaves a chain of empty folders down to it, and in a run without a command the
    /// top-level question reads that chain as holding something. A Temp clear that failed to spare a
    /// live entry, whose process works a few folders below it, is the case. It is not passed over,
    /// because the removal records what it left standing and <see cref="RunResidue.Entered"/> is asked
    /// first. What the choice still costs is the outside Clean in a run that holds a command as well,
    /// which reads as emptied, because nothing here can tell a tool's skeleton from MSBuild's.</para>
    ///
    /// <para>What was there before is captured through folders either way (see
    /// <see cref="DirectoryContent"/>), so a folder that held only empty folders is never the subject.
    /// Asked only of a directory that held something, so nothing here reads a path that was empty to
    /// begin with, and nothing reads a file.</para>
    /// </summary>
    private static bool WasEmptied(ProtectedPath protectedPath, RunReach reach) =>
        protectedPath.HeldContentBefore
        && !StillHoldsContent(protectedPath.Path, reach)
        && !HoldsAnyOf(protectedPath.Path, reach.TargetedPaths)
        && !HoldsAnyOf(protectedPath.Path, reach.ProbedPaths);

    /// <summary>The question <see cref="WasEmptied"/> asks after the run, at the depth what ran calls for.</summary>
    private static bool StillHoldsContent(string path, RunReach reach) =>
        reach.Unbounded ? DirectoryContent.IsPresent(path) : DirectoryContent.HoldsAnyEntry(path);

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
            && !HoldsAnyOf(parent, reach.TargetedPaths);
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
    /// Whether any of <paramref name="paths"/> sits inside <paramref name="folder"/>, which is the
    /// question <see cref="IsTargeted"/> asks the other way round. Asked of the run's targets, it is
    /// whether this run deleted anything inside the folder.
    ///
    /// <para>It is what stops the outside reading excusing the plainest over-reach there is:
    /// execution that goes one directory higher than the plan named. A step targeting
    /// <c>Proj\obj2</c> that took <c>Proj</c> with it leaves the deselected sibling <c>Proj\obj</c>
    /// missing, its folder missing, and neither of them under any target — every condition for
    /// "something else did it", about a directory this run was working inside.</para>
    /// </summary>
    private static bool HoldsAnyOf(string folder, IReadOnlyList<string> paths) =>
        paths.Any(path => LongPath.Contains(Display(folder), Display(path)));

    /// <summary>
    /// A path in the one form the comparisons above are valid in: no extended-length prefix, and no
    /// trailing separator to make a folder and its own name compare unequal.
    /// </summary>
    private static string Display(string path) =>
        Path.TrimEndingDirectorySeparator(LongPath.Display(path));
}
