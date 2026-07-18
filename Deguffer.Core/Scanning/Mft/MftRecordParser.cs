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
public static class MftRecordParser
{
    internal const uint AttributeFileName = 0x30;
    internal const uint AttributeData = 0x80;

    /// <summary>
    /// Parse one record. <paramref name="record"/> is modified in place by the update sequence
    /// fixup, so the caller must hand over a buffer it owns.
    ///
    /// A record does not report its own number here. The header field that holds it only exists on
    /// NTFS 3.1 and later, and the caller already knows the number from its position in the table —
    /// so reading it would add a version dependency to learn something nobody needs.
    ///
    /// Returns false for records that carry no size information for the tree — see
    /// <see cref="MftRecordHeader.TryRead"/> for which, and why each is excluded.
    /// </summary>
    public static bool TryParse(Span<byte> record, int bytesPerSector, out MftRecord result)
    {
        result = default;

        if (!MftRecordHeader.TryRead(record, bytesPerSector, out var header))
        {
            return false;
        }

        if (!TryReadAttributes(record[..header.UsedLength], header.FirstAttributeOffset, out var parsed))
        {
            return false;
        }

        // A directory's own $DATA is not the size of its contents — the contents are counted
        // through their own records — so attributing anything here would double-count them.
        result = new MftRecord(
            parsed.Parent,
            parsed.Name,
            header.IsDirectory ? ScanSize.Zero : parsed.Size,
            header.IsDirectory);

        return true;
    }

    private static bool TryReadAttributes(
        ReadOnlySpan<byte> record,
        int firstAttributeOffset,
        out (uint Parent, string Name, ScanSize Size) result)
    {
        result = default;

        uint parent = 0;
        var name = string.Empty;
        var size = ScanSize.Zero;
        var bestRank = int.MaxValue;

        var attributes = new MftAttributeEnumerator(record, firstAttributeOffset);

        while (attributes.MoveNext())
        {
            switch (attributes.CurrentType)
            {
                case AttributeFileName when TryReadFileName(attributes.Current, out var candidate):
                    // Prefer the Win32 name over the 8.3 alias: a long-named file carries several
                    // $FILE_NAME attributes, and picking the DOS alias would make path resolution
                    // fail against the name the user actually typed.
                    var rank = RankOf(candidate.Namespace);
                    if (rank < bestRank)
                    {
                        (parent, name, bestRank) = (candidate.Parent, candidate.Name, rank);
                    }

                    break;

                case AttributeData when IsUnnamed(attributes.Current):
                    size = ReadDataSize(attributes.Current);
                    break;
            }
        }

        if (attributes.IsMalformed || bestRank == int.MaxValue)
        {
            return false;
        }

        result = (parent, name, size);
        return true;
    }

    /// <summary>
    /// Only the unnamed <c>$DATA</c> stream is the file's size. Alternate data streams do occupy
    /// space, but attributing them to the file would make a scan disagree with what the user sees
    /// in Explorer, and they are vanishingly rare in the cache trees this tool targets.
    /// </summary>
    private static bool IsUnnamed(ReadOnlySpan<byte> attribute) => attribute[0x09] == 0;

    internal static ScanSize ReadDataSize(ReadOnlySpan<byte> attribute)
    {
        if (attribute[0x08] == 0)
        {
            // Resident data lives inside the MFT record itself, so it occupies no clusters of its
            // own. Allocated is genuinely zero: deleting such a file frees the record, not extents.
            return new ScanSize(Allocated: 0, Logical: BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]));
        }

        if (attribute.Length < 0x38)
        {
            return ScanSize.Zero;
        }

        // Only the first extent of a split attribute carries the sizes; later extents continue the
        // run list from a non-zero starting VCN and leave these fields zero.
        if (BinaryPrimitives.ReadUInt64LittleEndian(attribute[0x10..]) != 0)
        {
            return ScanSize.Zero;
        }

        var allocated = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x28..]);
        var logical = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);

        return allocated < 0 || logical < 0 ? ScanSize.Zero : new ScanSize(allocated, logical);
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

        result = (parent, Encoding.Unicode.GetString(value.Slice(0x42, nameLength)), (FileNameNamespace)value[0x41]);
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
