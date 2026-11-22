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
/// <param name="Cloud">
/// What describes the files a <see cref="ReleaseLocalCopiesStep"/> named. Asked only of a plan that
/// holds one.
/// </param>
/// <param name="Protection">
/// What lists the restore points, shadow copies and storage a <see cref="RemoveRestorePointsStep"/>
/// must leave.
/// </param>
/// <param name="Volumes">
/// Asked every other path a protected path and each of the run's paths are reachable at, because a step
/// may name a folder through a letter <c>subst</c> made, or another mount of its volume, that the
/// protected path does not.
/// </param>
/// <param name="Profiles">What lists the profiles a plan's <see cref="RemoveProfileStep"/>s must leave.</param>
/// <param name="Tuning">
/// What the walks of each volume run with (<see cref="IDirectoryScanner.Tuning"/>), which bounds how
/// many protected paths are checked at once on each. The shipped tuning where none is given.
/// </param>
public sealed record VerificationSources(
    ICloudFiles? Cloud = null,
    ISystemProtection? Protection = null,
    IVolumeInventory? Volumes = null,
    IUserProfiles? Profiles = null,
    ScanTuner? Tuning = null)
{
    /// <summary>The machine's own throughout, under the shipped tuning.</summary>
    public static VerificationSources Machine { get; } = new();
}
