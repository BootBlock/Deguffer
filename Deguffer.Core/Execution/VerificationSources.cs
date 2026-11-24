using Deguffer.Core.Cloud;
using Deguffer.Core.Profiles;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.SystemProtection;

namespace Deguffer.Core.Execution;

/// <summary>
/// What a §5.6 verification reads the machine through, beside its probes of each protected path. Every
/// one only reads, so each is the machine's own where none is given.
/// </summary>
/// <param name="Proofs">
/// What proves each step whose work the protected paths cannot, usually the
/// <see cref="StepRoute.Proof"/> of each route the plan ran through. A step none of them proves is
/// asked of the machine's own proof of its kind (<see cref="MachineProofs"/>), which only reads.
/// </param>
/// <param name="Volumes">
/// Asked every other path a protected path and each of the run's paths are reachable at, because a step
/// may name a folder through a letter <c>subst</c> made, or another mount of its volume, that the
/// protected path does not.
/// </param>
/// <param name="Tuning">
/// What the walks of each volume run with (<see cref="IDirectoryScanner.Tuning"/>), which bounds how
/// many protected paths are checked at once on each. The shipped tuning where none is given.
/// </param>
public sealed record VerificationSources(
    IReadOnlyList<StepProof>? Proofs = null,
    IVolumeInventory? Volumes = null,
    ScanTuner? Tuning = null)
{
    /// <summary>The machine's own throughout, under the shipped tuning.</summary>
    public static VerificationSources Machine { get; } = new();

    /// <summary>
    /// The machine's own proof of each kind of step that has one. They only read, so unlike the routes
    /// that act they are safe to default.
    /// </summary>
    internal static IReadOnlyList<StepProof> MachineProofs { get; } =
    [
        new ReleasedFileProof(CloudFiles.Default),
        new RestorePointProof(WindowsSystemProtection.Default),
        new ProfileRemovalProof(WindowsUserProfiles.Default),
    ];

    /// <summary>
    /// The proof a verification asks about each step that has one, grouped by proof in the order the
    /// proofs are given, each group's steps in the plan's order.
    /// </summary>
    internal IEnumerable<(StepProof Proof, IReadOnlyList<CleanupStep> Steps)> ProofsOf(IReadOnlyList<CleanupStep> steps)
    {
        IReadOnlyList<StepProof> proofs = [.. Proofs ?? [], .. MachineProofs];
        var proved = new List<CleanupStep>?[proofs.Count];

        foreach (var step in steps)
        {
            for (var i = 0; i < proofs.Count; i++)
            {
                if (proofs[i].Proves(step))
                {
                    (proved[i] ??= []).Add(step);
                    break;
                }
            }
        }

        for (var i = 0; i < proofs.Count; i++)
        {
            if (proved[i] is { } group)
            {
                yield return (proofs[i], group);
            }
        }
    }
}
