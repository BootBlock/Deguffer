using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring;

/// <summary>
/// When the Explore page may act: scan, relaunch elevated, or act on what is picked.
///
/// <para>One value for the three because they turn on the same work: a removal and a scan of the
/// same drive have no business overlapping, so each stands the other down through one busy flag, and
/// a page that asked that flag in three ways is how one of its buttons comes to stay live through
/// the work that should stop it.</para>
/// </summary>
/// <param name="Busy">Whether a scan or a removal is under way on the page.</param>
/// <param name="Replaying">
/// Whether the map shows a step of the replay of what grew rather than the drive as it is now.
/// </param>
public readonly record struct ExploreGate(bool Busy, bool Replaying)
{
    /// <summary>
    /// Whether what is picked may be acted on: only while nothing else is under way, and while the
    /// map shows the drive as it is now. A step of a replay is a picture of what the drive held, and
    /// the outlines of what is picked are not drawn on it, so nothing is acted on from it (§7.1).
    /// </summary>
    public bool SelectionMayAct => !Busy && !Replaying;

    /// <summary>
    /// Whether <paramref name="target"/> may be scanned now: nothing under way, and a target the page
    /// does not refuse. See <see cref="ExploreTarget.IsScannable"/>.
    /// </summary>
    public bool MayScan(ExploreTarget target, IVolumeInventory volumes) =>
        !Busy && target.IsScannable(volumes);

    /// <summary>
    /// Whether the page may relaunch elevated to scan <paramref name="target"/>: what a scan would
    /// cover, and nothing running on any page. Elevating ends this process, and a clean or a removal
    /// elsewhere would end with it (<see cref="RunningActions.MayEndProcess"/>).
    ///
    /// <para>The scan's answer rather than only the busy flag, because the replacement is pointed
    /// where the Scan button beside it would scan, and a relaunch with nothing to point at leaves the
    /// replacement waiting on a page the reader did not ask to be on.</para>
    /// </summary>
    public bool MayElevate(ExploreTarget target, IVolumeInventory volumes, RunningActions running)
    {
        ArgumentNullException.ThrowIfNull(running);

        return MayScan(target, volumes) && running.MayEndProcess;
    }
}
