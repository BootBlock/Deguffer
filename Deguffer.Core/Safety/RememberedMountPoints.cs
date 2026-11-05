using System.Collections.Concurrent;

namespace Deguffer.Core.Safety;

/// <summary>
/// The volumes, with every place a volume is mounted asked of the machine once per volume for the
/// life of its owner, which already keeps where each path it follows is reachable: a provider's pass,
/// one reading of the process table, one §5.6 verification, one reading of the installed programs,
/// one comparison of a clean's locations, and one Explore policy. Every other question goes to the
/// volumes it was built over, at the call.
///
/// <para><b>The question is about the drive, not the path.</b> Nearly every path a pass follows is on
/// one of a handful of drives, and <see cref="IVolumeInventory.MountPointsOf"/> costs two
/// mount-manager calls (#513). Asked live for each path, the same two or three answers were read for
/// every candidate, every running program's folders and every destroyed path.</para>
///
/// <para><b>Which volume a mount point names is asked at every call; only that volume's places are
/// remembered.</b> A drive letter or a folder can be given to another volume while the owner lives,
/// and an answer remembered by mount point would hand the new volume the old one's places, missing
/// every place a rule was written at. <see cref="IVolumeInventory.VolumeNameOf"/> names the volume
/// behind the mount point now, so a moved letter is a volume this memo has not seen and is asked
/// about. That keeps the cheaper of the two calls live and remembers the dearer one.</para>
///
/// <para><b>What a remembered answer can miss</b> is a new place for a volume already asked about,
/// and only for paths named through its old places. <see cref="IVolumeInventory.MountPointOf"/> stays
/// live, so a path named through the new place is answered with every place, the old ones included.
/// A containment asked of any pair of followed folders therefore still matches wherever it matched
/// before. A place the volume has lost since is still named, which can only add a refusal or a
/// match. A comparison with a folder named as text alone, such as the account's own folders, has no
/// second followed side, which is why the executor's last check before a removal asks the machine
/// itself.</para>
///
/// <para><b><see cref="IVolumeInventory.SubstituteOf"/> stays live.</b> It is one
/// <c>QueryDosDevice</c> of about two microseconds, so remembering it would save almost nothing, and
/// <c>subst</c> changes a letter without any signal the mount manager would see.</para>
///
/// <para>Never the <see cref="VolumeInventory.Current"/> singleton itself, which lives as long as the
/// app and would make a remembered answer as old as the session.</para>
/// </summary>
internal sealed class RememberedMountPoints(IVolumeInventory volumes) : IVolumeInventory
{
    /// <summary>Every place each volume is mounted, by its <c>\\?\Volume{GUID}\</c> name.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _mountPoints =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LocalVolume> Volumes => volumes.Volumes;

    public string? MountPointOf(string path) => volumes.MountPointOf(path);

    /// <summary>
    /// Every place the volume mounted at <paramref name="mountPoint"/> now is mounted, asked of the
    /// machine the first time that volume is asked about since this was built or last
    /// <see cref="Forget">forgot</see>.
    /// </summary>
    public IReadOnlyList<string> MountPointsOf(string mountPoint) =>
        VolumeNameOf(mountPoint) is { } name ? MountPointsOfVolume(name) : [];

    public string? VolumeNameOf(string mountPoint) => volumes.VolumeNameOf(mountPoint);

    public IReadOnlyList<string> MountPointsOfVolume(string volumeName) =>
        _mountPoints.GetOrAdd(volumeName, static (name, inner) => inner.MountPointsOfVolume(name), volumes);

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
