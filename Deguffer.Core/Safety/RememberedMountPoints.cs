using System.Collections.Concurrent;

namespace Deguffer.Core.Safety;

/// <summary>
/// The volumes, with <see cref="IVolumeInventory.MountPointsOf"/> asked of the machine once per mount
/// point for the life of its owner, which already keeps where each path it follows is reachable: a
/// provider's pass, one reading of the process table, one §5.6 verification, and one Explore policy.
/// Every other question goes to the volumes it was built over, at the call.
///
/// <para><b>The question is about the drive, not the path.</b> Nearly every path a pass follows is on
/// one of a handful of drives, and asking where <c>C:\</c> is mounted costs two mount-manager calls
/// (#513). Asked live for each path, the same two or three answers were read for every candidate,
/// every running program's folders and every destroyed path.</para>
///
/// <para><b>A volume mounted since is still seen where it decides anything.</b>
/// <see cref="IVolumeInventory.MountPointOf"/> stays live, so a path reached through a mount point
/// made after the owner's first question is answered with that new mount point, which is a key this
/// memo has never seen and therefore asks the machine about. What a remembered answer can miss is a
/// new place for a volume already asked about, and only for paths named through its old places. Two
/// such paths share their old text form, and a path named through the new place is answered with
/// every place, the old ones included, so a containment asked of any pair of followed folders still
/// matches wherever it matched before. A comparison with a folder named as text alone, such as the
/// account's own folders, has no such second side, which is why the executor's last check before a
/// removal asks the machine itself.</para>
///
/// <para><b><see cref="IVolumeInventory.SubstituteOf"/> stays live.</b> It is one
/// <c>QueryDosDevice</c> of about two microseconds against the fifty or more the mount manager takes,
/// so remembering it would save almost nothing, and <c>subst</c> changes a letter without any signal
/// the mount manager would see.</para>
///
/// <para>Never the <see cref="VolumeInventory.Current"/> singleton itself, which lives as long as the
/// app and would make a remembered answer as old as the session.</para>
/// </summary>
internal sealed class RememberedMountPoints(IVolumeInventory volumes) : IVolumeInventory
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _mountPoints =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LocalVolume> Volumes => volumes.Volumes;

    public string? MountPointOf(string path) => volumes.MountPointOf(path);

    /// <summary>
    /// Every place the volume at <paramref name="mountPoint"/> is mounted, asked of the machine the
    /// first time that mount point is asked about since this was built or last
    /// <see cref="Forget">forgot</see>.
    /// </summary>
    /// <param name="mountPoint">
    /// A mount point, with or without its trailing separator, which name the same directory and so
    /// share one answer.
    /// </param>
    public IReadOnlyList<string> MountPointsOf(string mountPoint) =>
        _mountPoints.GetOrAdd(
            Path.TrimEndingDirectorySeparator(mountPoint),
            static (_, asked) => asked.Volumes.MountPointsOf(asked.MountPoint),
            (Volumes: volumes, MountPoint: mountPoint));

    public string? SubstituteOf(string driveRoot) => volumes.SubstituteOf(driveRoot);

    public (long Total, long Free)? SpaceOf(string path) => volumes.SpaceOf(path);

    /// <summary>
    /// Discard the remembered list and every remembered answer, since the caller asked for what was
    /// mounted after them to be seen.
    /// </summary>
    public void Invalidate()
    {
        Forget();
        volumes.Invalidate();
    }

    public Task<IReadOnlyList<LocalVolume>> RefreshAsync(CancellationToken ct) => volumes.RefreshAsync(ct);

    /// <summary>
    /// Discard every remembered answer, for an owner that outlives one pass and drops what it follows
    /// at the start of the next. The list of volumes is left alone: it belongs to the volumes this was
    /// built over, which every provider shares.
    /// </summary>
    public void Forget() => _mountPoints.Clear();
}
