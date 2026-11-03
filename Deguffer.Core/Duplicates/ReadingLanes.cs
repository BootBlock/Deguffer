using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// Puts the volumes a content search reads into lanes, one a physical disk, each with its own bound
/// on how many files are read at once (§7.4).
///
/// <para><b>One lane a disk, not a volume.</b> Two volumes on one disk share its heads, and reading
/// both at once divides one disk's speed between them; on a spinning disk the seeks between them
/// make the pair much slower than either alone. So volumes that share any disk share a lane, and
/// lanes on different disks are read at the same time.</para>
///
/// <para><b>One reader wherever the disk is not known to be solid state</b>: a disk with a seek
/// penalty, removable media, a virtual or network disk, and a disk Windows did not describe. A
/// volume whose disks were not listed cannot be placed with any other, so it is a lane of its own,
/// read by one.</para>
/// </summary>
internal sealed class ReadingLanes
{
    /// <summary>
    /// The readers on a solid-state disk. Measured on an NVMe SSD reading 64 files of 64 MiB with
    /// unbuffered, aligned 1 MiB reads: throughput levelled off near 3.3 GB/s, which XXH128 reached
    /// with two readers and SHA-256 with four, and eight or sixteen added nothing. Four is the
    /// fewest that let the slowest common checksum keep pace with the disk. One thread hashes in
    /// memory at 54.6 GB/s by XXH128, 27.5 by CRC-32, 2.61 by SHA-256, 0.92 by SHA-1, 0.74 by
    /// SHA-512 and MD5, and 0.56 by SHA3-256, so the slowest of those stay bound by the processor
    /// even at four.
    /// </summary>
    public const int SolidStateReaders = 4;

    private readonly Dictionary<string, int> _laneOf;

    private ReadingLanes(IReadOnlyList<int> readers, Dictionary<string, int> laneOf)
    {
        Readers = readers;
        _laneOf = laneOf;
    }

    /// <summary>How many files each lane may read at once, by lane.</summary>
    public IReadOnlyList<int> Readers { get; }

    /// <summary>The lane that reads <paramref name="volume"/>, one of those it was made for.</summary>
    public int LaneOf(LocalVolume volume) => _laneOf[volume.RootPath];

    /// <param name="volumes">
    /// The volumes of the files to read, as the search resolved them
    /// (<see cref="DuplicateCandidate.Volume"/>), so no file sends a question to the machine. One may
    /// be given more than once.
    /// </param>
    public static ReadingLanes Of(IEnumerable<LocalVolume> volumes, VolumeMediaCache media)
    {
        Dictionary<string, LocalVolume> byRoot = new(StringComparer.OrdinalIgnoreCase);

        foreach (var volume in volumes)
        {
            byRoot.TryAdd(volume.RootPath, volume);
        }

        // Asked once a volume, and remembered by the cache for the life of the app.
        var sets = DiskSharing.Sets([.. byRoot.Values.Select(volume => (Volume: volume, Media: media.Of(volume)))], volume => volume.Media.PhysicalDisks);

        Dictionary<string, int> laneOf = new(StringComparer.OrdinalIgnoreCase);
        var readers = new int[sets.Count];

        for (var lane = 0; lane < sets.Count; lane++)
        {
            readers[lane] = sets[lane].All(volume => IsSolidState(volume.Media)) ? SolidStateReaders : 1;

            foreach (var (volume, _) in sets[lane])
            {
                laneOf[volume.RootPath] = lane;
            }
        }

        return new ReadingLanes(readers, laneOf);
    }

    /// <summary>
    /// Solid state with its disks listed. Without them the volume cannot be placed with the others
    /// on its disk, and several readers there could be several on one disk from more than one lane.
    /// </summary>
    private static bool IsSolidState(VolumeMedia media) =>
        media.Class is StorageMedia.Nvme or StorageMedia.SolidState && media.PhysicalDisks.Count > 0;
}
