using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Links;

/// <summary>
/// Decides whether what a link points at is there, keeping "gone" apart from "cannot tell".
///
/// <para><b>Gone is the one answer that is offered, so it is the one that has to be earned.</b> A
/// target is gone only where it is on a fixed local disk that Windows says is mounted and ready,
/// Windows answers that nothing is at the path, and no link stands on the way to it. Everything else is <see cref="LinkTargetState.Unreachable"/>:
/// a drive that is not there, a removable drive or a share (connected or not, since the one there now
/// need not be the one the link was made on), a refusal, and anything that is not a full path. A
/// refusal is never absence.</para>
///
/// <para>The volume is asked of the path through <see cref="IVolumeInventory.MountPointOf"/> rather than
/// read from its drive letter, because Windows answers a path through a junction with the volume on
/// the far side: a target under <c>C:\Data</c>, where <c>C:\Data</c> leads to a disk that is unplugged,
/// is on that disk, and reads absent only because the disk is.</para>
///
/// <para>One instance per operation (G4): a hundred shortcuts into one program's folder ask about each
/// target once, and about each volume once. Safe to share between threads.</para>
/// </summary>
public sealed class LinkTargets(IPathProbe probe, IVolumeInventory volumes)
{
    private const string VolumeNamePrefix = @"\\?\Volume{";

    private readonly ConcurrentDictionary<string, LinkTargetState> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _fixedMounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="target"/>, a full path in display form, is there.</summary>
    public LinkTargetState StateOf(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        return _targets.GetOrAdd(target, Decide);
    }

    /// <summary>
    /// The same question asked of the machine again, past what this instance remembers: the answer a
    /// removal stands on, which has to be the one there is now rather than the one the list was built on.
    /// </summary>
    public LinkTargetState StateNow(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        _fixedMounts.Clear();
        var state = Decide(target);
        _targets[target] = state;

        return state;
    }

    private LinkTargetState Decide(string target)
    {
        // A mounted volume's folder points at the volume by name. A volume that is not attached is a
        // disk that is unplugged, never one that is gone.
        if (target.StartsWith(VolumeNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return probe.ProbeEntry(target) is PathPresence.Present ? LinkTargetState.Present : LinkTargetState.Unreachable;
        }

        if (!Path.IsPathFullyQualified(target) || LongPath.IsDeviceSpelling(target) || LongPath.IsShare(target))
        {
            return LinkTargetState.Unreachable;
        }

        if (volumes.MountPointOf(target) is not { } mountPoint || !_fixedMounts.GetOrAdd(mountPoint, IsFixedAndReady))
        {
            return LinkTargetState.Unreachable;
        }

        return probe.ProbeEntry(target) switch
        {
            PathPresence.Present => LinkTargetState.Present,
            PathPresence.Absent => AbsenceIsReal(mountPoint, target) ? LinkTargetState.Gone : LinkTargetState.Unreachable,
            _ => LinkTargetState.Unreachable,
        };
    }

    /// <summary>
    /// Whether an absent <paramref name="target"/> is absent from the disk mounted at
    /// <paramref name="mountPoint"/>, rather than behind a link on the way to it.
    ///
    /// <para>Windows reads a share that does not answer and a drive that is not ready as "nothing is
    /// there", for every path through them. So a target under <c>C:\Users\testuser\Music</c>, where
    /// <c>Music</c> is a link to a NAS that is switched off, reads absent on a fixed disk that is ready.
    /// The walk down from the mount point stops at the first folder that is not there; a link or a
    /// refusal before it means the absence was not read on this disk, and the target cannot be told
    /// to be gone (see <see cref="DerivedPath.FirstObstacleBetween(string, string, IPathProbe)"/>). A
    /// mount point that is not a prefix of the target, which is how Windows answers a path through a
    /// junction onto another volume, is the same case.</para>
    /// </summary>
    private bool AbsenceIsReal(string mountPoint, string target) =>
        LongPath.Contains(mountPoint, target) && DerivedPath.FirstObstacleBetween(mountPoint, target, probe) is null;

    /// <summary>
    /// Whether the volume mounted at <paramref name="mountPoint"/> is a fixed local disk that is ready.
    /// A volume the list does not hold, one mounted since it was read included, is not known to be.
    /// </summary>
    private bool IsFixedAndReady(string mountPoint) =>
        volumes.Volumes.Any(volume =>
            volume is { Kind: DriveType.Fixed, IsLocalDisk: true }
            && volume.MountPoints.Any(place => HostVolume.IsMountPoint(place, mountPoint)));
}
