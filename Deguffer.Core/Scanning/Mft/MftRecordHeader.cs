using System.Buffers.Binary;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The fixed part of an MFT record: the few fields that must be read and validated before any
/// attribute can be trusted.
///
/// <see cref="Read"/> also applies the update sequence fixup, because there is no correct order
/// other than "before anything else" — every field beyond the header is wrong until it has run.
/// </summary>
/// <param name="Sequence">
/// How many times this record has been reused. A reference into the table carries the sequence it
/// expects, so a mismatch is a record that has since become something else.
/// </param>
/// <param name="BaseReference">
/// For an extension record, the base record that owns it. All zeroes for a base record.
/// </param>
internal readonly record struct MftRecordHeader(
    int FirstAttributeOffset,
    int UsedLength,
    bool IsDirectory,
    ushort Sequence,
    MftSegmentReference BaseReference)
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
    public static MftParseOutcome Read(Span<byte> record, int bytesPerSector, out MftRecordHeader header) =>
        Read(record, bytesPerSector, extension: false, out header);

    /// <summary>
    /// The same for an extension record, read only because a base record's <c>$ATTRIBUTE_LIST</c>
    /// named it. A base record is <see cref="MftParseOutcome.NotAnEntry"/> here, as an extension
    /// record is to <see cref="Read(Span{byte}, int, out MftRecordHeader)"/>.
    /// </summary>
    public static MftParseOutcome ReadExtension(Span<byte> record, int bytesPerSector, out MftRecordHeader header) =>
        Read(record, bytesPerSector, extension: true, out header);

    private static MftParseOutcome Read(Span<byte> record, int bytesPerSector, bool extension, out MftRecordHeader header)
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

        // An extension record's attributes are reached through the base record that owns them, so
        // parsing one as an entry of its own would count the same file twice. Checked before the
        // lengths below, so a damaged extension record met in passing costs nothing here: whether
        // it matters is for the base record that lists it to decide, when it follows its list.
        var baseReference = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]);
        if ((baseReference != 0) != extension)
        {
            return MftParseOutcome.NotAnEntry;
        }

        var used = BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]);
        var first = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);

        if (used > record.Length || first < MinimumLength || first >= used)
        {
            return MftParseOutcome.Unreadable;
        }

        header = new MftRecordHeader(
            first,
            (int)used,
            (flags & FlagDirectory) != 0,
            BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]),
            MftSegmentReference.FromRaw(baseReference));
        return MftParseOutcome.Parsed;
    }
}
