using System.Buffers.Binary;

namespace Deguffer.Core.Health.Parsing;

/// <summary>The small fixed descriptors a drive answers, each checked against what was returned.</summary>
internal static class DriveProperties
{
    /// <summary><c>STORAGE_DEVICE_NUMBER</c>: the device type, then the number.</summary>
    private const int DeviceNumberOffset = 4;

    /// <summary><c>DEVICE_TRIM_DESCRIPTOR</c>: version, size, then the one-byte flag.</summary>
    private const int TrimOffset = 8;

    /// <summary><c>STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR</c>'s logical, then physical, sector sizes.</summary>
    private const int LogicalSectorOffset = 16;

    private const int PhysicalSectorOffset = 20;

    /// <summary><c>DISK_GEOMETRY_EX</c>: a 24-byte <c>DISK_GEOMETRY</c>, then the size.</summary>
    private const int DiskSizeOffset = 24;

    internal static bool TryReadDeviceNumber(ReadOnlySpan<byte> bytes, out int disk)
    {
        disk = 0;

        if (bytes.Length < DeviceNumberOffset + sizeof(uint))
        {
            return false;
        }

        var number = BinaryPrimitives.ReadUInt32LittleEndian(bytes[DeviceNumberOffset..]);

        // 0xFFFFFFFF is how a device says it has no number.
        if (number > int.MaxValue)
        {
            return false;
        }

        disk = (int)number;
        return true;
    }

    internal static bool TryReadTrim(ReadOnlySpan<byte> bytes, out bool enabled)
    {
        enabled = false;

        if (bytes.Length <= TrimOffset)
        {
            return false;
        }

        enabled = bytes[TrimOffset] != 0;
        return true;
    }

    internal static bool TryReadSectors(ReadOnlySpan<byte> bytes, out SectorSizes sizes)
    {
        sizes = default;

        if (bytes.Length < PhysicalSectorOffset + sizeof(uint))
        {
            return false;
        }

        sizes = new SectorSizes(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[LogicalSectorOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[PhysicalSectorOffset..]));
        return true;
    }

    internal static bool TryReadDiskSize(ReadOnlySpan<byte> bytes, out long size)
    {
        size = 0;

        if (bytes.Length < DiskSizeOffset + sizeof(long))
        {
            return false;
        }

        size = BinaryPrimitives.ReadInt64LittleEndian(bytes[DiskSizeOffset..]);
        return true;
    }
}
