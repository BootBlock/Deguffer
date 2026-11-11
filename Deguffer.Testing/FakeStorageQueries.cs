using System.Buffers.Binary;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Testing;

/// <summary>
/// Volumes and disks described by synthesised descriptor bytes, so the classification is asserted
/// for buses and disks the machine running the suite does not have. A volume or disk nothing named
/// fails as a missing device does, with <c>ERROR_FILE_NOT_FOUND</c>.
/// </summary>
public sealed class FakeStorageQueries : IStorageQueries
{
    public const int ErrorFileNotFound = 2;

    private readonly Dictionary<string, StorageAnswer> _extents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, StorageAnswer> _adapters = [];
    private readonly Dictionary<int, StorageAnswer> _seekPenalties = [];
    private readonly Dictionary<int, StorageAnswer> _provisioning = [];

    public int ExtentsAsked { get; private set; }

    public int AdaptersAsked { get; private set; }

    public int SeekPenaltiesAsked { get; private set; }

    public int ProvisioningAsked { get; private set; }

    /// <summary>The volume at <paramref name="mountPoint"/> has one extent on each of <paramref name="disks"/>, in that order.</summary>
    public FakeStorageQueries Volume(string mountPoint, params int[] disks) =>
        VolumeAnswer(mountPoint, StorageAnswer.Answered(ExtentBytes(disks)));

    public FakeStorageQueries VolumeAnswer(string mountPoint, StorageAnswer extents)
    {
        _extents[mountPoint] = extents;
        return this;
    }

    /// <summary>
    /// <paramref name="disk"/> is on <paramref name="bus"/>, a <c>STORAGE_BUS_TYPE</c> value, and
    /// says it incurs a seek penalty or not, or fails the question where
    /// <paramref name="seekPenalty"/> is null.
    /// </summary>
    public FakeStorageQueries Disk(int disk, byte bus, bool? seekPenalty) =>
        DiskAnswers(
            disk,
            StorageAnswer.Answered(AdapterBytes(bus)),
            seekPenalty is { } incurs
                ? StorageAnswer.Answered(SeekPenaltyBytes(incurs))
                : StorageAnswer.Failed(ErrorFileNotFound));

    public FakeStorageQueries DiskAnswers(int disk, StorageAnswer adapter, StorageAnswer seekPenalty)
    {
        _adapters[disk] = adapter;
        _seekPenalties[disk] = seekPenalty;
        return this;
    }

    /// <summary>
    /// <paramref name="disk"/> says it is thinly provisioned or not, or fails the question where
    /// <paramref name="thin"/> is null. A disk nothing was said about fails it, as a missing device does.
    /// </summary>
    public FakeStorageQueries Thin(int disk, bool? thin) =>
        ProvisioningAnswer(
            disk,
            thin is { } enabled ? StorageAnswer.Answered(ProvisioningBytes(enabled)) : StorageAnswer.Failed(ErrorFileNotFound));

    public FakeStorageQueries ProvisioningAnswer(int disk, StorageAnswer provisioning)
    {
        _provisioning[disk] = provisioning;
        return this;
    }

    public StorageAnswer DiskExtents(string mountPoint)
    {
        ExtentsAsked++;
        return _extents.GetValueOrDefault(mountPoint, StorageAnswer.Failed(ErrorFileNotFound));
    }

    public StorageAnswer Adapter(int disk)
    {
        AdaptersAsked++;
        return _adapters.GetValueOrDefault(disk, StorageAnswer.Failed(ErrorFileNotFound));
    }

    public StorageAnswer SeekPenalty(int disk)
    {
        SeekPenaltiesAsked++;
        return _seekPenalties.GetValueOrDefault(disk, StorageAnswer.Failed(ErrorFileNotFound));
    }

    public StorageAnswer Provisioning(int disk)
    {
        ProvisioningAsked++;
        return _provisioning.GetValueOrDefault(disk, StorageAnswer.Failed(ErrorFileNotFound));
    }

    /// <summary>
    /// A <c>DEVICE_LB_PROVISIONING_DESCRIPTOR</c>, version and size 40, with every bit field beside
    /// <c>ThinProvisioningEnabled</c> set, so a read of the wrong bit answers the other way.
    /// </summary>
    public static byte[] ProvisioningBytes(bool thin)
    {
        var bytes = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 40);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 40);
        bytes[8] = (byte)(thin ? 0xFF : 0xFE);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 1UL << 20);
        return bytes;
    }

    /// <summary>A <c>VOLUME_DISK_EXTENTS</c> with one 1 GiB extent per disk named.</summary>
    public static byte[] ExtentBytes(params int[] disks)
    {
        var bytes = new byte[8 + (24 * disks.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)disks.Length);

        for (var i = 0; i < disks.Length; i++)
        {
            var extent = bytes.AsSpan(8 + (24 * i));
            BinaryPrimitives.WriteUInt32LittleEndian(extent, (uint)disks[i]);
            BinaryPrimitives.WriteInt64LittleEndian(extent[8..], (long)i << 30);
            BinaryPrimitives.WriteInt64LittleEndian(extent[16..], 1L << 30);
        }

        return bytes;
    }

    /// <summary>
    /// A <c>STORAGE_ADAPTER_DESCRIPTOR</c> as an NVMe disk's adapter answered on a real machine,
    /// with the bus byte replaced, so every field around the bus has a value a misplaced read would
    /// pick up instead.
    /// </summary>
    public static byte[] AdapterBytes(byte bus)
    {
        byte[] bytes =
        [
            0x20, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00, 0x01, 0x02, 0x00, 0x00,
            0x03, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01, 0x01, 0x11, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00,
        ];

        bytes[24] = bus;
        return bytes;
    }

    /// <summary>A <c>DEVICE_SEEK_PENALTY_DESCRIPTOR</c>, version and size 12, as Windows writes it.</summary>
    public static byte[] SeekPenaltyBytes(bool incursSeekPenalty) =>
        [0x0C, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, incursSeekPenalty ? (byte)1 : (byte)0, 0x00, 0x00, 0x00];
}
