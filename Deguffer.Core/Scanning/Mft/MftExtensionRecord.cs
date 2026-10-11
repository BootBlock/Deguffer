namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// What one extension record can add to the file that owns it, taken while the first pass holds the
/// record so the second pass does not read it from the disk again.
///
/// <para>Taken for every extension record in use, wanted or not. The first pass meets an extension
/// record before its owner as often as after it, on whichever parse thread, so it cannot yet know
/// which ones a list will name. Only those holding something the scan needs are kept, and one is a
/// small fraction of the record it describes.</para>
/// </summary>
/// <param name="Number">The record's position in the table.</param>
/// <param name="Sequence">How many times the record has been reused, which a list's reference to it must match.</param>
/// <param name="Owner">The base record the extension record says it belongs to.</param>
/// <param name="Readable">
/// False where the record's lengths or its attributes are damaged. Nothing below is then meaningful,
/// and a base record that wants it cannot establish what it wanted it for.
/// </param>
/// <param name="Supplied">The kinds of attribute the scan needs that the record holds.</param>
/// <param name="Attributes">The record's attributes, absorbed alone, ready to fold into its owner's.</param>
internal readonly record struct MftExtensionRecord(
    long Number,
    ushort Sequence,
    MftSegmentReference Owner,
    bool Readable,
    MftAttributeKinds Supplied,
    MftRecordDraft Attributes)
{
    /// <summary>
    /// What the record in <paramref name="record"/> holds, or null where it is not an extension
    /// record in use. Runs the fixup, which rewrites <paramref name="record"/> in place.
    /// </summary>
    public static MftExtensionRecord? TryRead(long number, Span<byte> record)
    {
        var outcome = MftRecordHeader.ReadEither(record, out var header);
        return header.IsExtension ? Of(number, outcome, header, record) : null;
    }

    /// <summary>What an extension record holds, once its header has been read and its fixup run.</summary>
    /// <param name="outcome">What reading the header made of it.</param>
    public static MftExtensionRecord Of(long number, MftParseOutcome outcome, in MftRecordHeader header, ReadOnlySpan<byte> record)
    {
        var attributes = new MftRecordDraft();
        var supplied = MftAttributeKinds.None;

        var readable = outcome == MftParseOutcome.Parsed
            && attributes.TryAbsorb(record[..header.UsedLength], header.FirstAttributeOffset, isBase: false, out supplied, out _);

        return new MftExtensionRecord(number, header.Sequence, header.BaseReference, readable, supplied, attributes);
    }
}
