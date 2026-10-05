using System.Buffers.Binary;

namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// Reads the three structures the storage queries return, from their bytes.
///
/// <para>Each reader checks the length it was given against the fields it reads, and answers false
/// rather than read past it: a driver that returns fewer bytes than the structure has said nothing
/// about the fields it left out, and a zero there would read as an answer.</para>
/// </summary>
internal static class StorageDescriptors
{
    /// <summary>
    /// <c>VOLUME_DISK_EXTENTS</c>: a 32-bit count, padded to 8 bytes, then that many
    /// <c>DISK_EXTENT</c>s of 24 bytes each, whose first 32 bits are the disk number.
    /// </summary>
    internal const int ExtentsHeaderBytes = 8;

    internal const int ExtentBytes = 24;

    /// <summary>
    /// <c>DEVICE_SEEK_PENALTY_DESCRIPTOR</c>: version, size, then the one-byte
    /// <c>IncursSeekPenalty</c>.
    /// </summary>
    private const int SeekPenaltyOffset = 8;

    /// <summary>
    /// <c>STORAGE_ADAPTER_DESCRIPTOR</c>: <c>BusType</c> is the byte after five 32-bit fields and four
    /// one-byte flags.
    /// </summary>
    private const int BusTypeOffset = 24;

    /// <summary>
    /// The count of extents a <c>VOLUME_DISK_EXTENTS</c> says it has, read from the header alone.
    /// For the query that sizes its buffer from a first answer that did not fit.
    /// </summary>
    internal static bool TryReadExtentCount(ReadOnlySpan<byte> bytes, out int count)
    {
        count = 0;

        if (bytes.Length < sizeof(uint))
        {
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes);

        // A count no buffer could hold is a malformed answer, not a volume of that many extents.
        if (declared > (int.MaxValue - ExtentsHeaderBytes) / ExtentBytes)
        {
            return false;
        }

        count = (int)declared;
        return true;
    }

    /// <summary>
    /// The disk numbers behind a volume, each once and in ascending order. A spanned or striped
    /// volume names the same disk once per extent on it, and the caller asks each disk once.
    /// </summary>
    internal static bool TryReadDiskNumbers(ReadOnlySpan<byte> bytes, out IReadOnlyList<int> disks)
    {
        disks = [];

        if (!TryReadExtentCount(bytes, out var count)
            || bytes.Length < ExtentsHeaderBytes + (count * ExtentBytes))
        {
            return false;
        }

        var numbers = new SortedSet<int>();

        for (var i = 0; i < count; i++)
        {
            var disk = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(ExtentsHeaderBytes + (i * ExtentBytes))..]);

            if (disk > int.MaxValue)
            {
                return false;
            }

            numbers.Add((int)disk);
        }

        disks = [.. numbers];
        return true;
    }

    internal static bool TryReadSeekPenalty(ReadOnlySpan<byte> bytes, out bool incursSeekPenalty)
    {
        incursSeekPenalty = false;

        if (bytes.Length <= SeekPenaltyOffset)
        {
            return false;
        }

        incursSeekPenalty = bytes[SeekPenaltyOffset] != 0;
        return true;
    }

    internal static bool TryReadBusType(ReadOnlySpan<byte> bytes, out StorageBusType bus)
    {
        bus = default;

        if (bytes.Length <= BusTypeOffset)
        {
            return false;
        }

        bus = (StorageBusType)bytes[BusTypeOffset];
        return true;
    }
}
