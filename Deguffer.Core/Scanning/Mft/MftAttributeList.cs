using System.Buffers.Binary;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One line of an <c>$ATTRIBUTE_LIST</c>: an attribute of the file, and the record it was moved to.
/// </summary>
/// <param name="Type">The attribute's type code, as in the record itself.</param>
/// <param name="IsNamed">
/// Whether the attribute carries a name. Only the unnamed <c>$DATA</c> is the file's size, so a
/// named stream listed here must not be mistaken for it.
/// </param>
/// <param name="LowestVcn">
/// The first virtual cluster this piece describes. A non-resident attribute split across records
/// lists one entry per piece, and only the piece at 0 states the attribute's sizes.
/// </param>
/// <param name="Segment">The record holding this piece, and the sequence number it must still have.</param>
internal readonly record struct MftAttributeListEntry(uint Type, bool IsNamed, long LowestVcn, MftSegmentReference Segment);

/// <summary>
/// Reads an <c>$ATTRIBUTE_LIST</c>: the index NTFS writes when a file's attributes no longer fit in
/// its base record, saying which record each one moved to.
///
/// <para>Bytes only. Following the list means reading other records, and sometimes clusters, which
/// is the job of whoever holds the <see cref="IMftSource"/>; the parser stays a pure function over
/// a span so every rule here is provable against a synthesised record.</para>
///
/// <para>Every offset and length is bounded by subtraction rather than by a sum. Each field here
/// comes off the disk, and a sum of two of them can wrap negative and pass a <c>&gt;</c> test.</para>
/// </summary>
internal static class MftAttributeList
{
    /// <summary>
    /// The largest list NTFS will write. A file that would need a longer one cannot be extended
    /// further, so a list declaring more than this is damage rather than a shape to follow — and
    /// honouring its length would size a buffer from a corrupt field.
    /// </summary>
    public const int MaximumLength = 256 * 1024;

    private const int MinimumEntryLength = 0x1A;

    /// <summary>
    /// The list's entries, or null where any entry is malformed. A list read in part is not a
    /// smaller list: the entry that could not be read may be the one naming where the size went.
    /// </summary>
    public static IReadOnlyList<MftAttributeListEntry>? TryReadEntries(ReadOnlySpan<byte> value)
    {
        var entries = new List<MftAttributeListEntry>();
        var offset = 0;

        while (offset < value.Length)
        {
            var remaining = value.Length - offset;
            if (remaining < MinimumEntryLength)
            {
                return null;
            }

            var entry = value[offset..];
            int length = BinaryPrimitives.ReadUInt16LittleEndian(entry[0x04..]);

            // Too short is the zero length that would spin here forever; too long reads past the list.
            if (length < MinimumEntryLength || length > remaining)
            {
                return null;
            }

            int nameLength = entry[0x06];
            int nameOffset = entry[0x07];

            if (nameLength > 0 && (nameOffset > length || nameLength * 2 > length - nameOffset))
            {
                return null;
            }

            var lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(entry[0x08..]);
            if (lowestVcn < 0)
            {
                return null;
            }

            entries.Add(new MftAttributeListEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(entry),
                nameLength > 0,
                lowestVcn,
                MftSegmentReference.FromRaw(BinaryPrimitives.ReadUInt64LittleEndian(entry[0x10..]))));

            offset += length;
        }

        return entries;
    }

    /// <summary>
    /// Where a non-resident list's bytes are on the volume, and how many of them are the list.
    ///
    /// <para>The list is clusters, not records, which is why reading it needs
    /// <see cref="IMftSource.TryReadClusters"/> rather than the record reads everything else here
    /// uses. A list is never split across records — it is the thing that says where the splits
    /// are — so a piece starting anywhere but cluster 0 is damage.</para>
    /// </summary>
    public static bool TryReadPlacement(ReadOnlySpan<byte> attribute, out IReadOnlyList<DataRun> runs, out int length)
    {
        runs = [];
        length = 0;

        if (attribute.Length < 0x40 || attribute[0x08] == 0)
        {
            return false;
        }

        int mappingPairsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..]);
        var dataSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);

        if (BinaryPrimitives.ReadUInt64LittleEndian(attribute[0x10..]) != 0
            || mappingPairsOffset >= attribute.Length
            || dataSize <= 0
            || dataSize > MaximumLength)
        {
            return false;
        }

        runs = DataRuns.Parse(attribute[mappingPairsOffset..]);
        length = (int)dataSize;

        return runs.Count > 0;
    }
}
