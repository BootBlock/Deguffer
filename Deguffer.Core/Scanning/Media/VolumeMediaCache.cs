using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// The storage behind each volume, asked once per volume and once per disk for the life of an
/// operation (G4), and asked again, remembered nowhere, where no earlier answer may settle the
/// question (<see cref="Now"/>).
///
/// <para>Its own type rather than a member of <see cref="LocalVolume"/>, because
/// <see cref="IVolumeInventory.Volumes"/> is read on the UI thread and this is not to be (#181):
/// every answer here opens a device and waits on it, and a volume list that carried the class would
/// pay for every disk each time a drive picker filled. A caller that has a volume and is off the UI
/// thread asks here.</para>
///
/// <para>Several volumes usually share one disk, so a disk's class is remembered apart from the
/// volumes, and a machine with eight letters on three disks asks three disks once each.</para>
/// </summary>
public sealed class VolumeMediaCache(IStorageQueries queries)
{
    private readonly Dictionary<string, Lazy<VolumeMedia>> _byVolume = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Lazy<DiskMedia>> _byDisk = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// What <paramref name="volume"/> sits on. Blocks on the device the first time a volume or a
    /// disk is asked about, so never call it on the UI thread.
    /// </summary>
    public VolumeMedia Of(LocalVolume volume) =>
        Remembered(_byVolume, volume.RootPath, () => MediaClassifier.ClassifyVolume(volume, queries, DiskOf));

    /// <summary>
    /// What <paramref name="volume"/> sits on now, asked of its disks again and remembered nowhere,
    /// for a question no earlier answer may settle: a disk moved from an internal bay into a USB dock
    /// keeps its volume and every file ID on it, and only its bus says it can now be unplugged. Blocks
    /// on the device, so never call it on the UI thread.
    /// </summary>
    public VolumeMedia Now(LocalVolume volume) =>
        MediaClassifier.ClassifyVolume(volume, queries, disk => MediaClassifier.ClassifyDisk(disk, queries));

    /// <summary>Forget every answer, so the next operation sees disks attached or swapped since.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _byVolume.Clear();
            _byDisk.Clear();
        }
    }

    private DiskMedia DiskOf(int disk) =>
        Remembered(_byDisk, disk, () => MediaClassifier.ClassifyDisk(disk, queries));

    /// <summary>
    /// The lock covers the dictionary alone, and the <see cref="Lazy{T}"/> makes two callers asking
    /// about one key wait for one answer, as <see cref="Mft.MftVolumeIndexCache"/> does: a volume on
    /// one disk does not wait for a slow answer from another.
    /// </summary>
    private T Remembered<TKey, T>(Dictionary<TKey, Lazy<T>> answers, TKey key, Func<T> ask)
        where TKey : notnull
    {
        Lazy<T> pending;

        lock (_gate)
        {
            if (!answers.TryGetValue(key, out pending!))
            {
                pending = new Lazy<T>(ask, LazyThreadSafetyMode.ExecutionAndPublication);
                answers[key] = pending;
            }
        }

        return pending.Value;
    }
}
