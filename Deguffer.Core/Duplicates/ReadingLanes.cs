using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Duplicates;

/// <summary>The files read by one set of readers, and how many may read at once.</summary>
internal sealed record ReadingLane<T>(int Readers, IReadOnlyList<T> Files);

/// <summary>
/// Puts the files a stage reads into lanes, one a physical disk, each with its own bound on how
/// many files are read at once (§7.4).
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
internal static class ReadingLanes
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

    /// <param name="volumeOf">
    /// The volume of the file an item reads, as the search resolved it
    /// (<see cref="DuplicateCandidate.Volume"/>), so no file sends a question to the machine.
    /// </param>
    public static IReadOnlyList<ReadingLane<T>> Of<T>(IEnumerable<T> items, Func<T, LocalVolume> volumeOf, VolumeMediaCache media)
    {
        Dictionary<string, (LocalVolume Volume, List<T> Files)> byVolume = new(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var volume = volumeOf(item);

            if (!byVolume.TryGetValue(volume.RootPath, out var held))
            {
                byVolume[volume.RootPath] = held = (volume, []);
            }

            held.Files.Add(item);
        }

        // Asked once a volume, and remembered by the cache for the life of the app.
        return
        [
            .. SharingADisk([.. byVolume.Values.Select(held => (held.Volume, media.Of(held.Volume)))])
                .Select(shared => new ReadingLane<T>(
                    shared.All(volume => IsSolidState(volume.Media)) ? SolidStateReaders : 1,
                    [.. shared.SelectMany(volume => byVolume[volume.Volume.RootPath].Files)])),
        ];
    }

    /// <summary>The volumes in sets, each set every volume that shares a disk with another in it.</summary>
    private static IEnumerable<IReadOnlyList<(LocalVolume Volume, VolumeMedia Media)>> SharingADisk(
        IReadOnlyList<(LocalVolume Volume, VolumeMedia Media)> volumes)
    {
        var setOf = Enumerable.Range(0, volumes.Count).ToArray();
        Dictionary<int, int> firstOnDisk = [];

        int Find(int volume) => setOf[volume] == volume ? volume : setOf[volume] = Find(setOf[volume]);

        for (var i = 0; i < volumes.Count; i++)
        {
            foreach (var disk in volumes[i].Media.PhysicalDisks)
            {
                if (firstOnDisk.TryGetValue(disk, out var other))
                {
                    setOf[Find(i)] = Find(other);
                }
                else
                {
                    firstOnDisk[disk] = i;
                }
            }
        }

        return Enumerable.Range(0, volumes.Count)
            .GroupBy(Find)
            .Select(set => (IReadOnlyList<(LocalVolume, VolumeMedia)>)[.. set.Select(i => volumes[i])]);
    }

    /// <summary>
    /// Solid state with its disks listed. Without them the volume cannot be placed with the others
    /// on its disk, and several readers there could be several on one disk from more than one lane.
    /// </summary>
    private static bool IsSolidState(VolumeMedia media) =>
        media.Class is StorageMedia.Nvme or StorageMedia.SolidState && media.PhysicalDisks.Count > 0;
}
