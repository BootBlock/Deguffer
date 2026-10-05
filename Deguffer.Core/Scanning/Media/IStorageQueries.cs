namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// The bytes one storage query returned, or the Win32 error it failed with.
/// </summary>
/// <param name="Bytes">What the driver wrote, cut to the length it reported, or null on failure.</param>
/// <param name="Win32Error">The error, or 0 where the query answered.</param>
public readonly record struct StorageAnswer(byte[]? Bytes, int Win32Error)
{
    public static StorageAnswer Answered(byte[] bytes) => new(bytes, 0);

    public static StorageAnswer Failed(int win32Error) => new(null, win32Error);
}

/// <summary>
/// The three questions Windows answers about the storage behind a volume, as raw bytes, so that
/// <see cref="VolumeMediaCache"/> classifies synthesised descriptors in a test and the real ones on
/// a machine by the same code.
///
/// <para>Each query opens its device with no access rights requested, so none of them needs an
/// administrator, and each blocks on the device, so none belongs on the UI thread.</para>
/// </summary>
public interface IStorageQueries
{
    /// <summary>
    /// <c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c> on the volume mounted at
    /// <paramref name="mountPoint"/>: the whole <c>VOLUME_DISK_EXTENTS</c>, however many extents it
    /// holds.
    /// </summary>
    StorageAnswer DiskExtents(string mountPoint);

    /// <summary>
    /// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> for <c>StorageAdapterProperty</c> on
    /// <c>\\.\PhysicalDrive<paramref name="disk"/></c>.
    /// </summary>
    StorageAnswer Adapter(int disk);

    /// <summary>
    /// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> for <c>StorageDeviceSeekPenaltyProperty</c> on
    /// <c>\\.\PhysicalDrive<paramref name="disk"/></c>.
    /// </summary>
    StorageAnswer SeekPenalty(int disk);
}
