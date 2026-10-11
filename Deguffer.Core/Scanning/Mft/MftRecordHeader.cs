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
    /// it turned out to be. An extension record is <see cref="MftParseOutcome.NotAnEntry"/> here.
    /// </summary>
    public static MftParseOutcome Read(Span<byte> record, out MftRecordHeader header) =>
        Only(extension: false, Read(record, out header, out var identified), identified, ref header);

    /// <summary>
    /// The same for an extension record, read only because a base record's <c>$ATTRIBUTE_LIST</c>
    /// named it. A base record is <see cref="MftParseOutcome.NotAnEntry"/> here, as an extension
    /// record is to <see cref="Read(Span{byte}, out MftRecordHeader)"/>.
    /// </summary>
    public static MftParseOutcome ReadExtension(Span<byte> record, out MftRecordHeader header) =>
        Only(extension: true, Read(record, out header, out var identified), identified, ref header);

    /// <summary>
    /// Validate and un-fixup a record of either kind, for a reader that keeps both: the first pass
    /// parses base records and keeps what extension records hold for the base records that want it.
    ///
    /// <para>The in-use flag is read before the fixup runs, which is safe — it sits at 0x16, nowhere
    /// near a fixup stride boundary — and necessary: a free record whose stale bytes fail the fixup
    /// is still just a free record, and reporting it as unreadable would condemn a healthy table.</para>
    ///
    /// <para>Once the fixup has run, <paramref name="header"/> names the record's sequence and owner
    /// even where its lengths are damaged and the outcome is <see cref="MftParseOutcome.Unreadable"/>.
    /// A damaged extension record is the concern of the base record that lists it, never of the
    /// table, so a caller has to be able to tell it from a damaged base record.</para>
    /// </summary>
    public static MftParseOutcome ReadEither(Span<byte> record, out MftRecordHeader header) =>
        Read(record, out header, out _);

    /// <param name="identified">Whether the fixup ran, so the header names the record's kind.</param>
    private static MftParseOutcome Read(Span<byte> record, out MftRecordHeader header, out bool identified)
    {
        header = default;
        identified = false;

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
                BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..])))
        {
            return MftParseOutcome.Unreadable;
        }

        identified = true;

        var used = BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]);
        var first = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        var valid = used <= record.Length && first >= MinimumLength && first < used;

        header = new MftRecordHeader(
            valid ? first : 0,
            valid ? (int)used : 0,
            (flags & FlagDirectory) != 0,
            BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]),
            MftSegmentReference.FromRaw(BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..])));

        return valid ? MftParseOutcome.Parsed : MftParseOutcome.Unreadable;
    }

    /// <summary>Whether this is an extension record, whose attributes belong to the base record that owns it.</summary>
    public bool IsExtension => BaseReference != default;

    /// <summary>
    /// An outcome of <see cref="ReadEither"/> for a reader that wants one kind of record. The other
    /// kind is <see cref="MftParseOutcome.NotAnEntry"/> whatever its lengths say, so a damaged
    /// extension record met in passing costs a base-record reader nothing: whether it matters is for
    /// the base record that lists it to decide, when it follows its list.
    /// </summary>
    private static MftParseOutcome Only(bool extension, MftParseOutcome outcome, bool identified, ref MftRecordHeader header)
    {
        if (identified && header.IsExtension != extension)
        {
            header = default;
            return MftParseOutcome.NotAnEntry;
        }

        return outcome;
    }
}
