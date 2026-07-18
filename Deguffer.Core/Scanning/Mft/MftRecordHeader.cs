using System.Buffers.Binary;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The fixed part of an MFT record: the few fields that must be read and validated before any
/// attribute can be trusted.
///
/// <see cref="TryRead"/> also applies the update sequence fixup, because there is no correct order
/// other than "before anything else" — every field beyond the header is wrong until it has run.
/// </summary>
internal readonly record struct MftRecordHeader(int FirstAttributeOffset, int UsedLength, bool IsDirectory)
{
    private static ReadOnlySpan<byte> Signature => "FILE"u8;

    private const int MinimumLength = 0x30;
    private const ushort FlagInUse = 0x0001;
    private const ushort FlagDirectory = 0x0002;

    /// <summary>
    /// Validate and un-fixup <paramref name="record"/> in place.
    ///
    /// Returns false for anything a size scan must not count: a record that is not a record, one
    /// that is free, one whose sectors disagree with their stamps, and extension records — whose
    /// attributes are already reachable from the base record that owns them, so parsing them
    /// separately would count the same file twice.
    /// </summary>
    public static bool TryRead(Span<byte> record, int bytesPerSector, out MftRecordHeader header)
    {
        header = default;

        if (record.Length < MinimumLength || !record[..4].SequenceEqual(Signature))
        {
            return false;
        }

        if (!UpdateSequenceArray.TryApply(
                record,
                BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..]),
                bytesPerSector))
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        if ((flags & FlagInUse) == 0)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) != 0)
        {
            return false;
        }

        var used = BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]);
        var first = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);

        if (used > record.Length || first < MinimumLength || first >= used)
        {
            return false;
        }

        header = new MftRecordHeader(first, (int)used, (flags & FlagDirectory) != 0);
        return true;
    }
}
