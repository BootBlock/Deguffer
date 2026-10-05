using System.Buffers.Binary;
using System.Text;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Turns the raw bytes of one MFT record into an <see cref="MftRecord"/>.
///
/// This is the seam the correctness of the whole fast path rests on, and it is deliberately a pure
/// function over a span: reading the MFT needs administrator rights (§6.3), so a parser that could
/// only be exercised against a live volume would be untestable on any ordinary build agent. Every
/// structural rule below is therefore provable against a synthesised record.
/// </summary>
internal static class MftRecordParser
{
    internal const uint AttributeStandardInformation = 0x10;
    internal const uint AttributeFileName = 0x30;
    internal const uint AttributeList = 0x20;
    internal const uint AttributeData = 0x80;
    internal const uint AttributeReparsePoint = 0xC0;

    /// <summary>
    /// Parse one record. <paramref name="record"/> is modified in place by the update sequence
    /// fixup, so the caller must hand over a buffer it owns.
    ///
    /// A record does not report its own number here. The header field that holds it only exists on
    /// NTFS 3.1 and later, and the caller already knows the number from its position in the table —
    /// so reading it would add a version dependency to learn something nobody needs.
    ///
    /// The outcomes are not interchangeable: see <see cref="MftParseOutcome"/> for why a record this
    /// cannot read is a different event from one there is nothing to read in.
    /// </summary>
    /// <param name="number">
    /// The record's position in the table. The record's own <c>$ATTRIBUTE_LIST</c> names the
    /// records its attributes went to by number, the base record among them.
    /// </param>
    /// <param name="deferred">
    /// Where the outcome is <see cref="MftParseOutcome.Continued"/>, what has to be read from other
    /// records before this one can be answered; otherwise null.
    /// </param>
    internal static MftParseOutcome Parse(
        Span<byte> record,
        long number,
        int bytesPerSector,
        out MftRecord result,
        out MftDeferredRecord? deferred)
    {
        result = default;
        deferred = null;

        var outcome = MftRecordHeader.Read(record, bytesPerSector, out var header);
        if (outcome != MftParseOutcome.Parsed)
        {
            return outcome;
        }

        var draft = new MftRecordDraft();
        var used = record[..header.UsedLength];

        if (!draft.TryAbsorb(used, header.FirstAttributeOffset, isBase: true, out _, out var listAt))
        {
            return MftParseOutcome.Unreadable;
        }

        if (listAt is not { } list)
        {
            return draft.Finish(header.IsDirectory, list: null, out result);
        }

        var pending = new MftDeferredRecord(number, header.Sequence, header.IsDirectory, draft);
        var attribute = used.Slice(list.Offset, list.Length);

        if (attribute[0x08] == 0)
        {
            pending.Follow(MftAttributeList.TryReadResidentValue(attribute, out var value)
                ? MftAttributeList.TryReadEntries(value)
                : null);
        }
        else if (MftAttributeList.TryReadPlacement(attribute, out var runs, out var length))
        {
            pending.AwaitList(runs, length);
        }
        else
        {
            pending.Follow(null);
        }

        if (pending.IsComplete)
        {
            return pending.Finish(out result);
        }

        deferred = pending;
        return MftParseOutcome.Continued;
    }

    /// <summary>
    /// The created and last-written times a <c>$STANDARD_INFORMATION</c> declares, as
    /// <c>FILETIME</c>s, or zeroes where this attribute does not declare them.
    ///
    /// <para>Zeroes rather than <see cref="MftParseOutcome.Unreadable"/>, and the asymmetry with
    /// <see cref="ReadDataSize"/> is deliberate. A size that cannot be read makes a subtree's total
    /// wrong, so it has to be reported as unknown and travel upward as one. A date that cannot be
    /// read costs the record nothing else: it still has its name, its parent and its size, and it
    /// still draws. Refusing a record for want of a date would take the fast path off a volume over
    /// a column, which is a trade nothing about a picture justifies.</para>
    ///
    /// <para>Zero is also what NTFS itself writes for a time it never set, so the two cases arrive
    /// as one value and both mean the same thing to a reader.</para>
    /// </summary>
    internal static (long Created, long LastWritten) ReadTimestamps(ReadOnlySpan<byte> attribute)
    {
        // Always resident on any volume NTFS wrote — it is 48 bytes at most and is the first
        // attribute of every record — so a non-resident one is a corrupt record rather than a shape
        // to follow. The enumerator admits an attribute of 0x10 bytes, which is shorter than the
        // resident header itself, so the length is checked before either field is read.
        if (attribute.Length < 0x18 || attribute[0x08] != 0)
        {
            return default;
        }

        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        var valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);

        // The two wanted are the first two fields of the value: created, then last written. The
        // other two NTFS keeps here — when the record itself last changed, and when the file was
        // last read — are deliberately not taken. The first dates bookkeeping rather than content,
        // and the second is the signal §8 rejected, because Windows stops maintaining it by
        // default.
        // Subtracted rather than added, because the sum overflows. A corrupt record can declare a
        // value length near int.MaxValue, and `valueOffset + valueLength` then wraps negative, passes
        // a `>` test and throws out of the slice below — which nothing on the scan path catches, so
        // it would take the window down. Reading this attribute at all is new, so this exposure is
        // new with it: the length is bounded by the record and the offset by a ushort, so neither
        // side of the subtraction can wrap.
        if (valueLength < 0x10 || valueOffset > attribute.Length - valueLength)
        {
            return default;
        }

        var value = attribute.Slice(valueOffset, valueLength);

        return (
            BinaryPrimitives.ReadInt64LittleEndian(value),
            BinaryPrimitives.ReadInt64LittleEndian(value[0x08..]));
    }

    /// <summary>
    /// Whether a <c>$REPARSE_POINT</c> means this entry stands for another name, which is the only
    /// kind of reparse point that makes an entry a link.
    ///
    /// The distinction decides a number. A file compressed with CompactOS carries a reparse point
    /// too, and its content is genuinely there: the filter driver hides the attribute from an
    /// ordinary enumeration, so the walk counts such a file, and an index that treated every
    /// reparse point as a link would report those bytes as nothing. The name-surrogate bit is what
    /// Windows itself uses to separate the two.
    ///
    /// An attribute too short to state a tag is not a link under any reading, and saying so keeps
    /// the two routes agreeing on it.
    /// </summary>
    internal static bool IsNameSurrogate(ReadOnlySpan<byte> attribute)
    {
        const uint NameSurrogateBit = 0x2000_0000;

        if (attribute.Length < 0x18)
        {
            return false;
        }

        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        if (valueOffset + 4 > attribute.Length)
        {
            return false;
        }

        return (BinaryPrimitives.ReadUInt32LittleEndian(attribute[valueOffset..]) & NameSurrogateBit) != 0;
    }

    /// <summary>
    /// Only the unnamed <c>$DATA</c> stream is the file's size. Alternate data streams do occupy
    /// space, but attributing them to the file would make a scan disagree with what the user sees
    /// in Explorer, and they are vanishingly rare in the cache trees this tool targets.
    /// </summary>
    internal static bool IsUnnamed(ReadOnlySpan<byte> attribute) => attribute[0x09] == 0;

    /// <summary>
    /// The sizes an unnamed <c>$DATA</c> declares, or null where this attribute does not declare
    /// them. Every null here is a file whose real size is somewhere the base record does not reach,
    /// so returning zero would silently subtract it from whatever subtree it belongs to.
    ///
    /// Both branches check their own length. The enumerator admits an attribute of 0x10 bytes,
    /// which is shorter than either header, and an unguarded read there throws out of a scan
    /// rather than reporting a size it could not establish.
    /// </summary>
    internal static ScanSize? ReadDataSize(ReadOnlySpan<byte> attribute)
    {
        if (attribute[0x08] == 0)
        {
            // Resident data lives inside the MFT record itself, so it occupies no clusters of its
            // own. Allocated is genuinely zero: deleting such a file frees the record, not extents.
            return attribute.Length < 0x18
                ? null
                : new ScanSize(Allocated: 0, Logical: BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]));
        }

        if (attribute.Length < 0x38)
        {
            return null;
        }

        // Only the first extent of a split attribute carries the sizes; later extents continue the
        // run list from a non-zero starting VCN and leave these fields zero.
        if (BinaryPrimitives.ReadUInt64LittleEndian(attribute[0x10..]) != 0)
        {
            return null;
        }

        var allocated = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x28..]);
        var logical = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);

        return allocated < 0 || logical < 0 ? null : new ScanSize(allocated, logical);
    }

    internal static bool TryReadFileName(
        ReadOnlySpan<byte> attribute,
        out (uint Parent, string Name, FileNameNamespace Namespace) result)
    {
        result = default;

        // $FILE_NAME is always resident; a non-resident one would mean a corrupt record.
        if (attribute[0x08] != 0 || attribute.Length < 0x18)
        {
            return false;
        }

        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        var valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);

        if (valueLength < 0x42 || valueOffset + valueLength > attribute.Length)
        {
            return false;
        }

        var value = attribute.Slice(valueOffset, valueLength);

        // A file reference packs a 48-bit record number under a 16-bit reuse sequence. Masking the
        // sequence off is what makes the parent usable as an index — but the remaining 48 bits can
        // still exceed what the index addresses, and narrowing that silently would wrap a distant
        // record onto an unrelated parent and graft a whole subtree somewhere it does not belong.
        var reference = BinaryPrimitives.ReadUInt64LittleEndian(value) & 0x0000_FFFF_FFFF_FFFF;
        if (reference > uint.MaxValue)
        {
            return false;
        }

        var parent = (uint)reference;

        int nameLength = value[0x40] * 2;
        if (0x42 + nameLength > value.Length)
        {
            return false;
        }

        result = (
            parent,
            Encoding.Unicode.GetString(value.Slice(0x42, nameLength)),
            (FileNameNamespace)value[0x41]);

        return true;
    }

    /// <summary>
    /// Preference between the several names one record can carry, best first. The on-disk byte is
    /// not itself an ordering — Win32AndDos is 3 and Posix is 0 — so ranking has to be explicit.
    /// A Posix name beats a bare DOS alias only because it is at least the real name; both are rare
    /// enough that the choice almost never arises.
    /// </summary>
    internal static int RankOf(FileNameNamespace value) => value switch
    {
        FileNameNamespace.Win32AndDos => 0,
        FileNameNamespace.Win32 => 1,
        FileNameNamespace.Posix => 2,
        _ => 3,
    };

    /// <summary>The values NTFS stores in the namespace byte. See <see cref="RankOf"/> for preference.</summary>
    internal enum FileNameNamespace : byte
    {
        Posix = 0,
        Win32 = 1,
        Dos = 2,
        Win32AndDos = 3,
    }
}
