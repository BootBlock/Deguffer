using System.Runtime.InteropServices;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning.Media;

/// <inheritdoc />
public sealed class StorageQueries : IStorageQueries
{
    /// <summary>The one instance the app runs with (G5). It holds nothing between calls.</summary>
    public static readonly StorageQueries Machine = new();

    /// <summary>
    /// <c>CTL_CODE(IOCTL_VOLUME_BASE, 0, METHOD_BUFFERED, FILE_ANY_ACCESS)</c>. Any access, which is
    /// why a handle opened with none can send it.
    /// </summary>
    private const uint VolumeGetVolumeDiskExtents = 0x0056_0000;

    private const int StorageAdapterProperty = 1;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    /// <summary>
    /// Room for four extents, which every simple volume and most spanned ones fit. A volume with
    /// more answers <c>ERROR_MORE_DATA</c> with its count, and is asked again with room for all.
    /// </summary>
    private const int FirstExtentsBytes = StorageDescriptors.ExtentsHeaderBytes + (4 * StorageDescriptors.ExtentBytes);

    /// <summary>
    /// Larger than either descriptor asked for: the seek-penalty one is 12 bytes and the adapter one
    /// 32. A driver writes no more than the structure, and reports how much it wrote.
    /// </summary>
    private const int DescriptorBytes = 64;

    private const int ErrorMoreData = 234;

    private StorageQueries()
    {
    }

    public StorageAnswer DiskExtents(string mountPoint)
    {
        // The volume's own name rather than \\.\X:, because a volume mounted at a folder has no
        // letter, and this is the name that opens a volume wherever it is mounted.
        if (VolumeCalls.VolumeNameOf(mountPoint, out var error) is not { } volumeName)
        {
            return StorageAnswer.Failed(error);
        }

        // The trailing separator names the volume's root directory. Without it the name opens the
        // volume device, which is what the query is sent to.
        using var volume = DeviceCalls.Open(volumeName.TrimEnd('\\'));

        if (volume.IsInvalid)
        {
            return StorageAnswer.Failed(Marshal.GetLastPInvokeError());
        }

        var first = new byte[FirstExtentsBytes];
        var answer = DeviceCalls.Query(volume, VolumeGetVolumeDiskExtents, [], first);

        // The header is written even when the extents do not fit, so the count it declares sizes the
        // retry. A retry that does not fit either fails like any other query.
        return answer.Win32Error == ErrorMoreData && StorageDescriptors.TryReadExtentCount(first, out var count)
            ? DeviceCalls.Query(volume, VolumeGetVolumeDiskExtents, [], new byte[StorageDescriptors.ExtentsHeaderBytes + (count * StorageDescriptors.ExtentBytes)])
            : answer;
    }

    public StorageAnswer Adapter(int disk) => Property(disk, StorageAdapterProperty);

    public StorageAnswer SeekPenalty(int disk) => Property(disk, StorageDeviceSeekPenaltyProperty);

    private static StorageAnswer Property(int disk, int property)
    {
        using var device = DeviceCalls.Open(DeviceCalls.DiskName(disk));

        return device.IsInvalid
            ? StorageAnswer.Failed(Marshal.GetLastPInvokeError())
            : DeviceCalls.Query(device, DeviceCalls.StorageQueryProperty, DeviceCalls.PropertyQuery(property), new byte[DescriptorBytes]);
    }
}
