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
    /// The three outcomes are not interchangeable: see <see cref="MftParseOutcome"/> for why a
    /// record this cannot read is a different event from one there is nothing to read in.
    /// </summary>
    internal static MftParseOutcome Parse(Span<byte> record, int bytesPerSector, out MftRecord result)
    {
        result = default;

        var outcome = MftRecordHeader.Read(record, bytesPerSector, out var header);
        if (outcome != MftParseOutcome.Parsed)
        {
            return outcome;
        }

        var attributes = ReadAttributes(record[..header.UsedLength], header.FirstAttributeOffset, out var parsed);
        if (attributes != MftParseOutcome.Parsed)
        {
            return attributes;
        }

        // A directory's own $DATA is not the size of its contents — the contents are counted
        // through their own records — so attributing anything here would double-count them.
        // Nothing is read from it, so a directory that keeps its attributes elsewhere is still a
        // known quantity: zero. Refusing there would give up on every large directory on the volume,
        // which is precisely where NTFS runs out of room in a record.
        result = new MftRecord(
            parsed.Parent,
            parsed.Name,
            header.IsDirectory ? ScanSize.Zero : parsed.Size,
            header.IsDirectory,
            parsed.IsReparsePoint);

        return MftParseOutcome.Parsed;
    }

    /// <summary>
    /// Read what the tree needs from one record's attributes, saying which of the three things this
    /// record turned out to be. A record can be in use and hold nothing placeable, and that is not
    /// the same as one this reader failed on.
    /// </summary>
    private static MftParseOutcome ReadAttributes(
        ReadOnlySpan<byte> record,
        int firstAttributeOffset,
        out (uint Parent, string Name, ScanSize? Size, bool IsReparsePoint) result)
    {
        result = default;

        uint parent = 0;
        var name = string.Empty;
        ScanSize? size = null;
        var sawData = false;
        var sawAttributeList = false;
        var sawReparsePoint = false;
        var bestRank = int.MaxValue;

        var walk = new MftAttributeEnumerator(record, firstAttributeOffset);

        while (walk.MoveNext())
        {
            switch (walk.CurrentType)
            {
                case AttributeFileName when TryReadFileName(walk.Current, out var candidate):
                    // Prefer the Win32 name over the 8.3 alias: a long-named file carries several
                    // $FILE_NAME attributes, and picking the DOS alias would make path resolution
                    // fail against the name the user actually typed.
                    var rank = RankOf(candidate.Namespace);
                    if (rank < bestRank)
                    {
                        (parent, name, bestRank) = (candidate.Parent, candidate.Name, rank);
                    }

                    break;

                case AttributeList:
                    sawAttributeList = true;
                    break;

                // The structure that makes an entry a junction or a link, rather than the flag for
                // it kept beside the name: NTFS refreshes those flags when the name changes rather
                // than when the file does, so a junction made over an existing directory can still
                // read as an ordinary one there. The attribute is the thing itself.
                case AttributeReparsePoint:
                    sawReparsePoint = IsNameSurrogate(walk.Current);
                    break;

                case AttributeData when IsUnnamed(walk.Current):
                    sawData = true;

                    // The first extent that establishes a size is the one to keep. A file split
                    // across extents lists the one starting at VCN 0 first — the only one carrying
                    // the sizes — and the continuations after it declare nothing. Assigning each in
                    // turn would let a continuation erase what the record had already established.
                    size ??= ReadDataSize(walk.Current);
                    break;
            }
        }

        if (walk.IsMalformed)
        {
            return MftParseOutcome.Unreadable;
        }

        if (bestRank == int.MaxValue)
        {
            // No name in this record. With an $ATTRIBUTE_LIST the names are in extension records —
            // what NTFS does once a file has enough hard links to overflow its own record, which a
            // system volume is full of.
            //
            // Skipping such a record is a compromise rather than a clean answer: the file is real,
            // so the directory holding it totals short by however much it occupies, and the total
            // is not marked approximate. Refusing instead would take the fast path off any volume
            // holding one, which on C: means always. Following the attribute list to the extension
            // record that holds the name is the answer that costs nothing, and it is a larger piece
            // of work than the one this rule sits in.
            //
            // Without a list, a record in use claims no identity and points nowhere else for one,
            // which no healthy volume produces.
            return sawAttributeList ? MftParseOutcome.NotAnEntry : MftParseOutcome.Unreadable;
        }

        if (!sawData)
        {
            // No unnamed $DATA here. With an $ATTRIBUTE_LIST present it is in an extension record
            // and this record cannot say how large the file is; with no list at all there is
            // genuinely no unnamed stream — a symbolic link, say — and zero is the true answer.
            size = sawAttributeList ? null : ScanSize.Zero;
        }

        result = (parent, name, size, sawReparsePoint);
        return MftParseOutcome.Parsed;
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
    private static bool IsNameSurrogate(ReadOnlySpan<byte> attribute)
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
    private static bool IsUnnamed(ReadOnlySpan<byte> attribute) => attribute[0x09] == 0;

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

    private static bool TryReadFileName(
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
    private static int RankOf(FileNameNamespace value) => value switch
    {
        FileNameNamespace.Win32AndDos => 0,
        FileNameNamespace.Win32 => 1,
        FileNameNamespace.Posix => 2,
        _ => 3,
    };

    /// <summary>The values NTFS stores in the namespace byte. See <see cref="RankOf"/> for preference.</summary>
    private enum FileNameNamespace : byte
    {
        Posix = 0,
        Win32 = 1,
        Dos = 2,
        Win32AndDos = 3,
    }
}
