using Deguffer.Core.Cloud;
using Deguffer.Core.Profiles;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.SystemProtection;

namespace Deguffer.Core.Execution;

/// <summary>
/// §5.6 — assert that the things that should have survived did.
///
/// This is policy, not mechanism: what counts as evidence of survival is a safety decision, and
/// it is kept apart from execution so it can be read and changed without touching it. This type
/// decides what a plan is held to; <see cref="SurvivalCheck"/> judges each protected path.
/// </summary>
public static class PlanVerifier
{
    /// <param name="runReach">
    /// What the whole run may destroy, which is what a disappearance is measured against. Null
    /// means this plan is the whole run, which is true of a provider verified on its own.
    /// </param>
    /// <param name="residue">
    /// What the run's removals have left standing so far, and which spared entries they left alone.
    /// Null means nothing was removed, which is true of a verification with no execution behind it.
    /// </param>
    /// <param name="sources">What the verification reads the machine through. The machine's own where none is given.</param>
    public static VerificationResult Verify(
        CleanupPlan plan,
        RunReach? runReach = null,
        RunResidue? residue = null,
        CancellationToken ct = default,
        VerificationSources? sources = null) =>
        Verify(plan, runReach, residue, sources ?? VerificationSources.Machine, LongPath.ProbeEntry, ct);

    /// <summary>
    /// The same, with each protected path's presence after the run given by <paramref name="probe"/>:
    /// the seam a test blocks or counts the disk's answers through.
    ///
    /// <para><b>Several protected paths at once, under the disk's own number</b>
    /// (<see cref="DiskBoundWork"/>). Each check probes its path and may list it or probe a marker
    /// inside it, a clean can protect ten thousand paths, and the clean's result waits on them all.
    /// What each check proves does not depend on another's, and every one is still made, in the plan's
    /// order, so the report reads as it did when they were made one after another.</para>
    /// </summary>
    internal static VerificationResult Verify(
        CleanupPlan plan,
        RunReach? runReach,
        RunResidue? residue,
        VerificationSources sources,
        Func<string, PathPresence> probe,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var inventory = sources.Volumes ?? VolumeInventory.Current;
        var places = runReach?.PlacesOn(inventory) ?? new RunPlaces(RunReach.Of([plan]), inventory);

        // Read once for the whole verification rather than once for each protected path. A plan is
        // verified once its own removals are over, and the run starts the next plan only after that,
        // so the record does not grow while it is read.
        var enteredRoots = residue?.EnteredRoots ?? [];

        List<VerificationCheck> checks =
        [
            .. DiskBoundWork.Each(
                plan.ProtectedPaths,
                protectedPath => protectedPath.Path,
                (sources.Tuning ?? ScanTuner.Shipped).For,
                ListingBudget.Shared,
                protectedPath => SurvivalCheck.Of(protectedPath, places, residue, enteredRoots, probe),
                ct),
        ];

        foreach (var release in plan.Steps.OfType<ReleaseLocalCopiesStep>())
        {
            checks.AddRange(ReleasedFileProof.Checks(
                release, sources.Cloud ?? CloudFiles.Default, missing => SurvivalCheck.Of(missing, places, residue, enteredRoots, probe), ct));
        }

        foreach (var removal in plan.Steps.OfType<RemoveRestorePointsStep>())
        {
            ct.ThrowIfCancellationRequested();
            checks.AddRange(RestorePointProof.Checks(removal, sources.Protection ?? WindowsSystemProtection.Default));
        }

        if (plan.Steps.OfType<RemoveProfileStep>().ToList() is { Count: > 0 } removals)
        {
            ct.ThrowIfCancellationRequested();
            checks.AddRange(ProfileRemovalProof.Checks(removals, sources.Profiles ?? WindowsUserProfiles.Default));
        }

        return new VerificationResult { Checks = checks };
    }
}
