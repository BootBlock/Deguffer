using Deguffer.Core.SystemProtection;

namespace Deguffer.Core.Execution;

/// <summary>
/// Ask System Restore to remove the restore points the plan named, one at a time, with its own
/// <c>SRRemoveRestorePoint</c> (§5.1). Deguffer never names a shadow copy or a path: System Restore
/// removes what it keeps for each restore point.
///
/// <para><b>The plan names the restore points, and the run takes no others.</b> A restore point made
/// between the preview and the clean is not one the user saw, so it stays. The run also never removes
/// whatever restore point is newest when it reaches the step, so the machine keeps one way back even if
/// the one the preview kept has gone in the meantime.</para>
///
/// <para><b>Its §5.6 negative is not about paths.</b> What must survive is the newest restore point,
/// every shadow copy that cannot be a restore point's, and System Protection's limit on each volume.
/// The step carries what each of those was when the plan was made, and
/// <see cref="RestorePointProof"/> asks Windows again afterwards.</para>
/// </summary>
/// <param name="Removes">The restore points to remove, oldest first.</param>
/// <param name="Kept">The newest restore point when the plan was made, which is never removed.</param>
/// <param name="What">What the user is told this step will do.</param>
public sealed record RemoveRestorePointsStep(
    IReadOnlyList<RestorePoint> Removes,
    RestorePoint Kept,
    string What) : CleanupStep
{
    /// <summary>
    /// Every shadow copy that cannot be a restore point's, which removing restore points must leave.
    /// See <see cref="ShadowCopy.CouldBeRestorePoint"/> for why the set can be stated in only that
    /// direction.
    /// </summary>
    public IReadOnlyList<ShadowCopy> OtherCopies { get; init; } = [];

    /// <summary>
    /// The shadow copy storage on each volume when the plan was made. Its limit is System Protection's
    /// setting, which removing restore points must leave as it was.
    /// </summary>
    public IReadOnlyList<VolumeShadowStorage> Storage { get; init; } = [];

    public override string Description => What;

    /// <summary>
    /// One key for the row's one step: which restore points are older than the newest changes with every
    /// one Windows makes, and a key that moved with them would discard the user's choice each time.
    /// </summary>
    public override string SelectionKey => "restore-points";

    /// <summary>No path: what this step is about is named by System Restore, never by where it is kept.</summary>
    public override IReadOnlyList<string> Subjects => [];
}
