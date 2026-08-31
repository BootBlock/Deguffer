using System.Buffers.Binary;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The fixed part of an MFT record: the few fields that must be read and validated before any
/// attribute can be trusted.
///
/// <see cref="Read"/> also applies the update sequence fixup, because there is no correct order
/// other than "before anything else" — every field beyond the header is wrong until it has run.
/// </summary>
internal readonly record struct MftRecordHeader(int FirstAttributeOffset, int UsedLength, bool IsDirectory)
{
    private static ReadOnlySpan<byte> Signature => "FILE"u8;

    private const int MinimumLength = 0x30;
    private const ushort FlagInUse = 0x0001;
    private const ushort FlagDirectory = 0x0002;

    /// <summary>
    /// Validate and un-fixup <paramref name="record"/> in place, saying which of the three things
    /// it turned out to be.
    ///
    /// The in-use flag is read before the fixup runs, which is safe — it sits at 0x16, nowhere near
    /// a sector boundary — and necessary: a free record whose stale bytes fail the fixup is still
    /// just a free record, and reporting it as unreadable would condemn a healthy table.
    /// </summary>
    public static MftParseOutcome Read(Span<byte> record, int bytesPerSector, out MftRecordHeader header)
    {
        header = default;

        if (record.Length < MinimumLength || !record[..4].SequenceEqual(Signature))
        {
            return MftParseOutcome.NotAnEntry;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        if ((flags & FlagInUse) == 0)
        {
            return MftParseOutcome.NotAnEntry;
        }

        if (!UpdateSequenceArray.TryApply(
                record,
                BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..]),
                bytesPerSector))
        {
            return MftParseOutcome.Unreadable;
        }

        // An extension record's attributes are already reachable from the base record that owns
        // them, so parsing this one separately would count the same file twice.
        if (BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) != 0)
        {
            return MftParseOutcome.NotAnEntry;
        }

        var used = BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]);
        var first = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);

        if (used > record.Length || first < MinimumLength || first >= used)
        {
            return MftParseOutcome.Unreadable;
        }

        header = new MftRecordHeader(first, (int)used, (flags & FlagDirectory) != 0);
        return MftParseOutcome.Parsed;
    }
}
