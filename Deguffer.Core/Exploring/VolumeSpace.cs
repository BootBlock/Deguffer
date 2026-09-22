using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring;

/// <summary>
/// How large a scanned volume is and how much of it is free, for the blocks a treemap draws beside
/// a scan of the whole of it. <see cref="None"/> where the scan did not cover a whole volume.
///
/// <para>Only for a whole volume. Free space is in proportion to a volume, and beside one folder of
/// it the block would say the folder shares its volume with that much room, which is true of every
/// folder on it and tells the reader nothing about this one — and on a nearly empty drive it would
/// shrink the folder the reader asked about to a corner.</para>
///
/// <para>The capacity comes with it because free space alone would overstate itself. What a scan
/// counts is a lower bound (§7.1): folders it could not read, and the file system's own records,
/// are in use and in no folder. Laid beside the scan, free space would take the share of the
/// picture those bytes belong to, so the treemap draws them as a block of their own, worked out
/// from the capacity.</para>
/// </summary>
/// <param name="TotalBytes">The volume's capacity.</param>
/// <param name="FreeBytes">
/// What is left of it for this user, which is what <see cref="LocalVolume.FreeBytes"/> reports.
/// </param>
public readonly record struct VolumeSpace(long TotalBytes, long FreeBytes)
{
    public static VolumeSpace None { get; } = new(0, 0);

    /// <summary>
    /// The space on the volume <paramref name="scannedRoot"/> is the top of, or <see cref="None"/>
    /// where it is a folder inside a volume, where no volume in <paramref name="volumes"/> holds it,
    /// or where the volume would not state both figures.
    ///
    /// <para>Whether the root is the top of its volume is <see cref="VolumeRoot"/>'s question, which
    /// answers for a volume mounted at a folder and for each of a volume's mount points, and is
    /// written once for the safety rules that ask it too.</para>
    /// </summary>
    public static VolumeSpace Of(IVolumeInventory volumes, string scannedRoot)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        return Path.IsPathFullyQualified(scannedRoot)
            && VolumeRoot.Below(volumes, scannedRoot) is null
            && HostVolume.For(volumes, scannedRoot) is { TotalBytes: { } total, FreeBytes: { } free }
                ? new VolumeSpace(total, free)
                : None;
    }

    /// <summary>
    /// What the volume says is in use and <paramref name="scannedBytes"/> does not include, or zero
    /// where the scan counted as much as that or more. A scan can count more, because it adds up
    /// the length of every file, and a compressed or sparse file occupies less than its length.
    /// </summary>
    public long UnaccountedBytes(long scannedBytes) => Math.Max(0, TotalBytes - FreeBytes - scannedBytes);
}
