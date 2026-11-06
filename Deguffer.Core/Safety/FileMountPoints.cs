using System.Collections.Concurrent;

namespace Deguffer.Core.Safety;

/// <summary>
/// The volumes, with where each of a known set of files is mounted asked of the machine once for the
/// folder holding it, for the life of its owner. Every other question, and every other path, goes to
/// the volumes it was built over, at the call (#539).
///
/// <para><b>Every file in one folder is answered as the first one asked about.</b>
/// <see cref="IVolumeInventory.MountPointOf"/> walks a path's folders up to the volume they are on, and
/// two files in one folder differ only in a name that is not a folder, so nothing that answer is read
/// from differs between them. A duplicate search asks it for every copy, and a folder of twenty thousand
/// copies asked twenty thousand times for one answer.</para>
///
/// <para><b>Files only, because only a folder can be a mount point.</b> A folder asked about by the
/// answer of its parent would miss a volume mounted at the folder itself, and would read the whole volume
/// as a thing inside the folder it is mounted at. So the owner names the files it will ask about, and a
/// path it did not name, a folder or a file reached through a link included, is asked about live.</para>
///
/// <para><b>What a remembered answer can miss is nothing that is still there.</b> Windows mounts a volume
/// only at an empty folder, and makes a folder a link only while it is empty, so no folder above a file
/// gains or loses a mount point while the file is in it. A drive letter given to another volume names the
/// same mount point. A folder emptied, or moved and replaced, no longer holds the files the answer was
/// for, and a removal finds the file it identified at a path before it removes it (§7.4). The owner
/// already keeps each path's whole answer for as long as this keeps the folder's.</para>
///
/// <para>Never the <see cref="VolumeInventory.Current"/> singleton itself, as
/// <see cref="RememberedMountPoints"/> is not.</para>
/// </summary>
internal sealed class FileMountPoints : IVolumeInventory
{
    private readonly IVolumeInventory _volumes;

    /// <summary>The files this may answer by their folder, in display form.</summary>
    private readonly HashSet<string> _files;

    /// <summary>Where each folder holding one of <see cref="_files"/> is mounted, as the first file in it was answered.</summary>
    private readonly ConcurrentDictionary<string, string?> _byFolder =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="volumes">The volumes every question goes to on its first asking.</param>
    /// <param name="files">Every file the owner will ask about, in display form.</param>
    public FileMountPoints(IVolumeInventory volumes, IEnumerable<string> files)
    {
        _volumes = volumes;
        _files = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<LocalVolume> Volumes => _volumes.Volumes;

    /// <summary>
    /// Where the volume holding <paramref name="path"/> is mounted: as the first file of its folder was
    /// answered, where it is one of the files this was given, and asked of the machine otherwise.
    /// </summary>
    public string? MountPointOf(string path)
    {
        var display = LongPath.Display(path);

        if (!_files.Contains(display) || Path.GetDirectoryName(display) is not { Length: > 0 } folder)
        {
            return _volumes.MountPointOf(path);
        }

        if (_byFolder.TryGetValue(folder, out var known))
        {
            return known;
        }

        // Two threads asking at once each get the machine's answer, so the first one stored stands.
        return _byFolder.GetOrAdd(folder, _volumes.MountPointOf(path));
    }

    public string? VolumeNameOf(string mountPoint) => _volumes.VolumeNameOf(mountPoint);

    public IReadOnlyList<string> MountPointsOfVolume(string volumeName) => _volumes.MountPointsOfVolume(volumeName);

    public string? SubstituteOf(string driveRoot) => _volumes.SubstituteOf(driveRoot);

    public (long Total, long Free)? SpaceOf(string path) => _volumes.SpaceOf(path);

    /// <summary>
    /// Discard every remembered answer and the list of volumes, since the caller asked for what was
    /// mounted after them to be seen.
    /// </summary>
    public void Invalidate()
    {
        _byFolder.Clear();
        _volumes.Invalidate();
    }

    public Task<IReadOnlyList<LocalVolume>> RefreshAsync(CancellationToken ct) => _volumes.RefreshAsync(ct);
}
