using System.Collections.Concurrent;

namespace Deguffer.Core.Safety;

/// <summary>
/// The volumes, with the folder each drive letter stands for asked of the machine once per letter for
/// the life of one measurement. Every other question goes to the volumes it was built over, at the call.
///
/// <para><b>Why one measurement may remember what <see cref="RememberedMountPoints"/> asks live.</b>
/// Before a measurement walks anything it asks what every path is on, to choose each walk's values
/// and to keep the walks of one disk in turn, and it asks that of each path two to four times. One
/// <c>QueryDosDevice</c> is cheap, but a measurement of a few thousand paths on one drive asked the
/// same letter thousands of times for one answer (#1065). A measurement takes milliseconds to seconds,
/// and every path of it is placed from the answers read at its start whether or not they are
/// remembered, so a letter <c>subst</c> changes during it moves no path the measurement would
/// otherwise have placed differently.</para>
///
/// <para>Never kept past the measurement that built it, so the next measurement asks each letter
/// again, and a provider's pass and a clean's checks never see these answers.</para>
/// </summary>
internal sealed class RememberedSubstitutes(IVolumeInventory volumes) : IVolumeInventory
{
    /// <summary>The folder each drive root stands for, or null where it stands for none.</summary>
    private readonly ConcurrentDictionary<string, string?> _substitutes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LocalVolume> Volumes => volumes.Volumes;

    public string? MountPointOf(string path) => volumes.MountPointOf(path);

    public string? VolumeNameOf(string mountPoint) => volumes.VolumeNameOf(mountPoint);

    public IReadOnlyList<string> MountPointsOfVolume(string volumeName) => volumes.MountPointsOfVolume(volumeName);

    /// <summary>
    /// The folder <paramref name="driveRoot"/> stands for, asked of the machine the first time it is
    /// asked about. A letter that stands for no folder is remembered too, since that is the answer
    /// nearly every path of a measurement gets.
    /// </summary>
    public string? SubstituteOf(string driveRoot)
    {
        if (_substitutes.TryGetValue(driveRoot, out var known))
        {
            return known;
        }

        return _substitutes.GetOrAdd(driveRoot, volumes.SubstituteOf(driveRoot));
    }

    public (long Total, long Free)? SpaceOf(string path) => volumes.SpaceOf(path);

    /// <summary>
    /// Discard every remembered answer and the list, since the caller asked for what was mounted after
    /// them to be seen.
    /// </summary>
    public void Invalidate()
    {
        _substitutes.Clear();
        volumes.Invalidate();
    }

    public Task<IReadOnlyList<LocalVolume>> RefreshAsync(CancellationToken ct) => volumes.RefreshAsync(ct);
}
