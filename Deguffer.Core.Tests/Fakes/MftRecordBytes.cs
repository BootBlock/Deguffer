using System.Buffers.Binary;
using System.Text;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>Where a record keeps the bytes of its unnamed <c>$DATA</c>, and whether they can be read.</summary>
public enum DataPlacement
{
    NonResident,
    Resident,
    NoData,
    InExtensionRecord,
    SplitAcrossExtents,
    LaterExtent,
    TruncatedHeader,
    TruncatedResidentHeader,
}

/// <summary>
/// The bytes of one MFT record, laid out as NTFS lays them out.
///
/// Separate from <see cref="MftFixture"/> because assembling a table and encoding a record are
/// different jobs: the fixture decides which entries a volume has, and this decides what one of
/// them looks like on disk. Keeping them apart is what lets a new on-disk shape be added here
/// without the table assembly growing a case for it.
///
/// This is a genuine inverse of the reader rather than a stub: it writes real attribute records and
/// applies a real update sequence array, so a test measuring a fixture tree exercises the same
/// parsing that runs against a live volume.
/// </summary>
internal static class MftRecordBytes
{
    public const int BytesPerSector = 512;
    public const int BytesPerRecord = 1024;

    /// <summary>The attribute flag NTFS sets on a junction or symbolic link.</summary>
    private const uint FileAttributeReparsePoint = 0x0400;

    private const int UsaOffset = 0x30;

    /// <summary>
    /// Every fixture directory carries a non-zero <c>$DATA</c> stream, and it is deliberately not
    /// zero: a reader that counted a directory's own data would double every file beneath it, and
    /// against zero-sized directory streams that bug is invisible.
    /// </summary>
    public const long DirectoryStreamBytes = 512;

    public static byte[] Build(
        ulong parentReference,
        string name,
        bool isDirectory,
        long allocated,
        long logical,
        DataPlacement placement,
        bool isReparsePoint = false)
    {
        var record = new byte[BytesPerRecord];
        var span = record.AsSpan();

        var usaCount = (BytesPerRecord / BytesPerSector) + 1;
        var firstAttribute = Align8(UsaOffset + (usaCount * 2));

        "FILE"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x04..], UsaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x06..], (ushort)usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x14..], (ushort)firstAttribute);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x16..], (ushort)(isDirectory ? 0x0003 : 0x0001));
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x1C..], BytesPerRecord);

        var offset = firstAttribute;
        offset += WriteFileName(
            span[offset..],
            parentReference,
            name,
            allocated,
            logical,
            isReparsePoint ? FileAttributeReparsePoint : 0);

        offset += WriteData(span[offset..], allocated, logical, placement);

        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], 0xFFFF_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], (uint)(offset + 8));

        ApplyFixup(span, usaCount);
        return record;
    }

    /// <summary>
    /// A record whose attributes belong to another record's file. NTFS writes these whenever one
    /// record runs out of room, so a real volume is full of them and none of them is a fault.
    /// </summary>
    public static byte[] ExtensionRecord(uint baseRecordNumber)
    {
        var record = new byte[BytesPerRecord];
        var span = record.AsSpan();

        var usaCount = (BytesPerRecord / BytesPerSector) + 1;
        var firstAttribute = Align8(UsaOffset + (usaCount * 2));

        "FILE"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x04..], UsaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x06..], (ushort)usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x14..], (ushort)firstAttribute);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x16..], 0x0001);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x20..], baseRecordNumber | (1UL << 48));
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x1C..], BytesPerRecord);
        BinaryPrimitives.WriteUInt32LittleEndian(span[firstAttribute..], 0xFFFF_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], (uint)(firstAttribute + 8));

        ApplyFixup(span, usaCount);
        return record;
    }

    /// <summary>
    /// Record 0 — the entry <c>$MFT</c> keeps about itself, which is where the reader learns where
    /// the rest of the table physically lives. Built here rather than in a test so it carries a
    /// real update sequence array and a real mapping pair list.
    /// </summary>
    public static byte[] SelfRecord(IReadOnlyList<DataRun> runs, long dataSize, bool withAttributeList = false)
    {
        var record = new byte[BytesPerRecord];
        var span = record.AsSpan();

        var usaCount = (BytesPerRecord / BytesPerSector) + 1;
        var offset = Align8(UsaOffset + (usaCount * 2));

        "FILE"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x04..], UsaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x06..], (ushort)usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x14..], (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x16..], 0x0001);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x1C..], BytesPerRecord);

        if (withAttributeList)
        {
            const int ListLength = 0x28;
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], 0x20);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], ListLength);
            span[offset + 0x08] = 1;
            offset += ListLength;
        }

        offset += WriteMftData(span[offset..], runs, dataSize);

        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], 0xFFFF_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], (uint)(offset + 8));

        ApplyFixup(span, usaCount);
        return record;
    }

    /// <summary>
    /// Solve for the name length that pushes <c>$DATA</c>'s allocated field over byte 510. Derived
    /// rather than hard-coded so it stays correct if the record layout above is ever adjusted.
    /// </summary>
    public static int NameLengthPuttingSizeFieldAcrossBoundary()
    {
        var boundary = BytesPerSector - 2;
        var firstAttribute = Align8(0x30 + (((BytesPerRecord / BytesPerSector) + 1) * 2));

        for (var length = 1; length < 255; length++)
        {
            var dataStart = firstAttribute + Align8(0x18 + 0x42 + (length * 2));
            var allocatedField = dataStart + 0x28;

            if (allocatedField <= boundary && boundary < allocatedField + 8)
            {
                return length;
            }
        }

        throw new InvalidOperationException(
            "No file name length places a $DATA size field across the sector boundary; the fixup test would be vacuous.");
    }

    /// <summary>
    /// The exact inverse of <see cref="UpdateSequenceArray.TryApply"/>: displace the last two bytes
    /// of every sector into the array and stamp the sequence number in their place.
    /// </summary>
    public static void ApplyFixup(Span<byte> record, int usaCount)
    {
        const ushort Stamp = 0x5A5A;

        var array = record.Slice(UsaOffset, usaCount * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(array, Stamp);

        for (var i = 0; i < usaCount - 1; i++)
        {
            var tail = record.Slice(((i + 1) * BytesPerSector) - 2, 2);
            tail.CopyTo(array[((i + 1) * 2)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(tail, Stamp);
        }
    }

    private static int WriteData(Span<byte> target, long allocated, long logical, DataPlacement placement) =>
        placement switch
        {
            DataPlacement.Resident => WriteResidentData(target, (int)logical),
            DataPlacement.NoData => 0,
            DataPlacement.InExtensionRecord => WriteAttributeList(target),
            DataPlacement.SplitAcrossExtents => WriteSplitData(target, allocated, logical),
            DataPlacement.LaterExtent => WriteNonResidentData(target, allocated, logical, startVirtualCluster: 4),
            DataPlacement.TruncatedHeader => WriteTruncatedData(target, 0x30, resident: false),
            DataPlacement.TruncatedResidentHeader => WriteTruncatedData(target, 0x10, resident: true),
            _ => WriteNonResidentData(target, allocated, logical, startVirtualCluster: 0),
        };

    private static int WriteFileName(
        Span<byte> target,
        ulong parentReference,
        string name,
        long allocated,
        long logical,
        uint fileAttributes)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var valueLength = 0x42 + nameBytes.Length;
        var length = Align8(0x18 + valueLength);

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);
        target[0x08] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x0A..], 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x10..], (uint)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x14..], 0x18);

        var value = target.Slice(0x18, valueLength);

        BinaryPrimitives.WriteUInt64LittleEndian(value, parentReference);
        BinaryPrimitives.WriteInt64LittleEndian(value[0x28..], allocated);
        BinaryPrimitives.WriteInt64LittleEndian(value[0x30..], logical);
        BinaryPrimitives.WriteUInt32LittleEndian(value[0x38..], fileAttributes);
        value[0x40] = (byte)name.Length;
        value[0x41] = 3; // Win32AndDos
        nameBytes.CopyTo(value[0x42..]);

        return length;
    }

    private static int WriteNonResidentData(Span<byte> target, long allocated, long logical, long startVirtualCluster)
    {
        const int Length = 0x48;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], Length);
        target[0x08] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(target[0x10..], (ulong)startVirtualCluster);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x20..], 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x28..], allocated);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x30..], logical);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x38..], logical);

        return Length;
    }

    /// <summary>
    /// A file too fragmented for one record: an attribute list, the extent starting at VCN 0 that
    /// carries the sizes, and a continuation extent that does not. NTFS writes them in this order,
    /// and the sizes are still fully known from the first one.
    /// </summary>
    private static int WriteSplitData(Span<byte> target, long allocated, long logical)
    {
        var written = WriteAttributeList(target);
        written += WriteNonResidentData(target[written..], allocated, logical, startVirtualCluster: 0);
        written += WriteNonResidentData(target[written..], allocated: 0, logical: 0, startVirtualCluster: 4);

        return written;
    }

    /// <summary>
    /// A resident <c>$ATTRIBUTE_LIST</c>, standing in for the index NTFS writes when a record's
    /// attributes no longer fit in it. Its contents are not read — what the reader has to notice is
    /// that the record has one at all, and so may be describing itself somewhere else.
    /// </summary>
    private static int WriteAttributeList(Span<byte> target)
    {
        const int Length = 0x28;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], Length);
        target[0x08] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x10..], Length - 0x18);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x14..], 0x18);

        return Length;
    }

    /// <summary>
    /// A <c>$DATA</c> whose declared length stops before the fields a reader wants from it. The
    /// enumerator admits any attribute of at least 0x10 bytes, so a reader that indexes past that
    /// without checking throws rather than reporting an unknown size.
    /// </summary>
    private static int WriteTruncatedData(Span<byte> target, int length, bool resident)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);
        target[0x08] = (byte)(resident ? 0 : 1);

        return length;
    }

    private static int WriteResidentData(Span<byte> target, int valueLength)
    {
        var length = Align8(0x18 + valueLength);

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);
        target[0x08] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x10..], (uint)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x14..], 0x18);

        return length;
    }

    private static int WriteMftData(Span<byte> target, IReadOnlyList<DataRun> runs, long dataSize)
    {
        const int RunsOffset = 0x40;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x80);
        target[0x08] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(target[0x20..], RunsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x28..], dataSize);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x30..], dataSize);
        BinaryPrimitives.WriteInt64LittleEndian(target[0x38..], dataSize);

        var cursor = RunsOffset;
        long previous = 0;

        foreach (var run in runs)
        {
            // A four-byte length and a four-byte signed delta: not the most compact encoding NTFS
            // would choose, but a legal one, which is what the reader has to cope with.
            target[cursor++] = 0x44;
            BinaryPrimitives.WriteInt32LittleEndian(target[cursor..], (int)run.ClusterCount);
            cursor += 4;
            BinaryPrimitives.WriteInt32LittleEndian(target[cursor..], (int)(run.StartCluster - previous));
            cursor += 4;
            previous = run.StartCluster;
        }

        target[cursor++] = 0x00;

        var length = Align8(cursor);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], (uint)length);

        return length;
    }

    private static int Align8(int value) => (value + 7) & ~7;
}
