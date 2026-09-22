using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring;

/// <summary>
/// How much free space to draw beside a scan, which is the volume's free space where the scan
/// covered the whole volume and nothing otherwise.
///
/// <para>Free space is in proportion to a whole volume. Beside one folder of it, the block would say
/// the folder shares its volume with that much room, which is true of every folder on it and tells
/// the reader nothing about this one — and on a nearly empty drive it would shrink the folder the
/// reader asked about to a corner.</para>
/// </summary>
public static class VolumeFreeSpace
{
    /// <summary>
    /// The free space on the volume <paramref name="scannedRoot"/> is the top of, or zero where it is
    /// a folder inside a volume, where no volume in <paramref name="volumes"/> holds it, or where the
    /// volume would not say.
    ///
    /// <para>Any of the volume's mount points counts, because a volume mounted at a folder as well as
    /// at a drive letter is the whole of itself at either, and a folder the reader picked is compared
    /// without its trailing separator, which a picker leaves off and the inventory puts on.</para>
    /// </summary>
    public static long Beside(IVolumeInventory volumes, string scannedRoot)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentException.ThrowIfNullOrWhiteSpace(scannedRoot);

        if (HostVolume.For(volumes, scannedRoot) is not { FreeBytes: { } free } volume)
        {
            return 0;
        }

        var root = Path.TrimEndingDirectorySeparator(LongPath.Display(scannedRoot));

        foreach (var mountPoint in volume.MountPoints)
        {
            if (string.Equals(
                Path.TrimEndingDirectorySeparator(mountPoint), root, StringComparison.OrdinalIgnoreCase))
            {
                return free;
            }
        }

        return 0;
    }
}
