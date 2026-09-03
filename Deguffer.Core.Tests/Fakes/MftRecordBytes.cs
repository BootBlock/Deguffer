using System.Buffers.Binary;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// One MFT record, assembled from the attributes it holds.
///
/// Separate from <see cref="MftFixture"/> because assembling a table and encoding a record are
/// different jobs: the fixture decides which entries a volume has, and this decides what one of
/// them looks like on disk. Keeping them apart is what lets a new on-disk shape be added here
/// without the table assembly growing a case for it.
///
/// This is a genuine inverse of the reader rather than a stub: it writes real attributes and
/// applies a real update sequence array, so a test measuring a fixture tree exercises the same
/// parsing that runs against a live volume.
/// </summary>
internal static class MftRecordBytes
{
    public const int BytesPerSector = 512;
    public const int BytesPerRecord = 1024;

    /// <summary>
    /// Every fixture directory carries a non-zero <c>$DATA</c> stream, and it is deliberately not
    /// zero: a reader that counted a directory's own data would double every file beneath it, and
    /// against zero-sized directory streams that bug is invisible.
    /// </summary>
    public const long DirectoryStreamBytes = 512;

    private const int UsaOffset = 0x30;

    /// <summary>Windows' own tags: the two that stand for another name, and one that does not.</summary>
    public const uint MountPointTag = 0xA000_0003;

    public const uint SymbolicLinkTag = 0xA000_000C;

    /// <summary>
    /// A file whose content is compressed in place by the Windows Overlay Filter — CompactOS, or
    /// <c>compact /c /exe</c>. Its bytes are genuinely there and the filter hides the reparse point
    /// from an ordinary enumeration, so a walk counts such a file like any other.
    /// </summary>
    public const uint WindowsOverlayFilterTag = 0x8000_0017;

    /// <param name="created">
    /// The <c>$STANDARD_INFORMATION</c> creation time, as a <c>FILETIME</c>. Zero is what NTFS
    /// itself writes for a time it never set, so it is the honest default for a fixture that is not
    /// about dates.
    /// </param>
    /// <param name="lastWritten">The last data change time, in the same units.</param>
    public static byte[] Build(
        ulong parentReference,
        string name,
        bool isDirectory,
        long allocated,
        long logical,
        DataPlacement placement,
        uint reparseTag = 0,
        long created = 0,
        long lastWritten = 0)
    {
        var record = new byte[BytesPerRecord];
        var span = record.AsSpan();
        var offset = WriteHeader(span, (ushort)(isDirectory ? 0x0003 : 0x0001), baseReference: 0);

        // First, which is where NTFS puts it, and on every record rather than only the dated ones.
        // A fixture that wrote it when a test asked about dates and not otherwise would be modelling
        // an idealised volume again — the same mistake that let reserved records 12 to 15 go
        // untested for six weeks. The builders below carry one for the same reason; only the two
        // shapes that are *defined* by a missing attribute go without.
        offset += MftAttributeBytes.WriteStandardInformation(span[offset..], created, lastWritten);

        offset += MftAttributeBytes.WriteFileName(span[offset..], parentReference, name, allocated, logical);

        if (reparseTag != 0)
        {
            offset += MftAttributeBytes.WriteReparsePoint(span[offset..], reparseTag);
        }

        offset += MftAttributeBytes.WriteData(span[offset..], allocated, logical, placement);

        return Close(record, offset);
    }

    /// <summary>
    /// A record whose attributes belong to another record's file. NTFS writes these whenever one
    /// record runs out of room, so a real volume is full of them and none of them is a fault.
    ///
    /// <para>No <c>$STANDARD_INFORMATION</c>, and that is the format rather than an omission: the
    /// times belong to the base record that owns this one.</para>
    /// </summary>
    public static byte[] ExtensionRecord(uint baseRecordNumber)
    {
        var record = new byte[BytesPerRecord];
        var span = record.AsSpan();
        var offset = WriteHeader(span, flags: 0x0001, baseReference: baseRecordNumber | (1UL << 48));

        return Close(record, offset);
    }

    /// <summary>
    /// A file with no <c>$STANDARD_INFORMATION</c> at all — the shape a reader must date as unknown
    /// rather than refuse.
    ///
    /// <para>Nothing else about such a record is in doubt: it has a name, a parent and a size, and
    /// it draws. Refusing it would take §5.5's fast path off a whole volume over a column, and the
    /// reserved records NTFS leaves nameless are the standing reminder of what a reader that gives
    /// up too readily costs.</para>
    /// </summary>
    public static byte[] FileWithoutTimestamps(ulong parentReference, string name, long logical)
    {
        var record = new byte[BytesPerRecord];
        var span = record.AsSpan();
        var offset = WriteHeader(span, flags: 0x0001, baseReference: 0);

        offset += MftAttributeBytes.WriteFileName(span[offset..], parentReference, name, logical, logical);
        offset += MftAttributeBytes.WriteData(span[offset..], logical, logical, DataPlacement.NonResident);

        return Close(record, offset);
    }

    /// <summary>
    /// A base record carrying no <c>$FILE_NAME</c>, which happens when a file has enough hard links
    /// to overflow its own record and NTFS moves the names into extension records. Common on a
    /// system volume, and not a fault: the record is in use and simply cannot be placed from here.
    ///
    /// Without the attribute list it is a different thing entirely — a record in use that carries
    /// no identity at all, which no healthy volume produces.
    /// </summary>
    public static byte[] RecordWithoutAName(bool withAttributeList)
    {
        var record = new byte[BytesPerRecord];
        var span = record.AsSpan();
        var offset = WriteHeader(span, flags: 0x0001, baseReference: 0);

        // Dated like any other record. This shape stands in for reserved records 12 to 15 among
        // others, and those carry times on a real volume — the thing that makes them unreadable is
        // the missing $FILE_NAME, not a missing $STANDARD_INFORMATION. Leaving it out here would
        // have made the four records the reader's carve-out exists for the least realistic ones in
        // the fixture.
        offset += MftAttributeBytes.WriteStandardInformation(span[offset..], created: 0, lastWritten: 0);

        if (withAttributeList)
        {
            offset += MftAttributeBytes.WriteAttributeList(span[offset..]);
        }

        offset += MftAttributeBytes.WriteData(span[offset..], allocated: 4096, logical: 4096, DataPlacement.NonResident);

        return Close(record, offset);
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
        var offset = WriteHeader(span, flags: 0x0001, baseReference: 0);

        // Record 0 is a real file with real times, so it carries the attribute like the rest. The
        // run list that follows is located by an offset within its own attribute, so starting it
        // further into the record changes nothing about how it is read.
        offset += MftAttributeBytes.WriteStandardInformation(span[offset..], created: 0, lastWritten: 0);

        if (withAttributeList)
        {
            offset += MftAttributeBytes.WriteAttributeList(span[offset..]);
        }

        offset += MftAttributeBytes.WriteMftData(span[offset..], runs, dataSize);

        return Close(record, offset);
    }

    /// <summary>
    /// Solve for the name length that pushes <c>$DATA</c>'s allocated field over byte 510. Derived
    /// rather than hard-coded so it stays correct if the record layout above is ever adjusted.
    /// </summary>
    public static int NameLengthPuttingSizeFieldAcrossBoundary()
    {
        var boundary = BytesPerSector - 2;

        // $STANDARD_INFORMATION sits between the header and the name, so the name does not start at
        // the first attribute offset. Derived rather than hard-coded so it stays correct if the
        // record layout is ever adjusted — which is exactly what adding that attribute was.
        var firstAttribute = FirstAttributeOffset() + MftAttributeBytes.StandardInformationLength;

        for (var length = 1; length < 255; length++)
        {
            var dataStart = firstAttribute + MftAttributeBytes.Align8(0x18 + 0x42 + (length * 2));
            var allocatedField = dataStart + 0x28;

            if (allocatedField <= boundary && boundary < allocatedField + 8)
            {
                return length;
            }
        }

        throw new InvalidOperationException(
            "No file name length places a $DATA size field across the sector boundary; the fixup test would be vacuous.");
    }

    private static int UsaCount => (BytesPerRecord / BytesPerSector) + 1;

    private static int FirstAttributeOffset() => MftAttributeBytes.Align8(UsaOffset + (UsaCount * 2));

    /// <summary>The fixed part every record starts with. Returns the offset its attributes begin at.</summary>
    private static int WriteHeader(Span<byte> record, ushort flags, ulong baseReference)
    {
        var firstAttribute = FirstAttributeOffset();

        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x04..], UsaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x06..], (ushort)UsaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x14..], (ushort)firstAttribute);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x16..], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x20..], baseReference);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x1C..], BytesPerRecord);

        return firstAttribute;
    }

    /// <summary>Terminate the attribute list, record how much of the record is in use, and fix it up.</summary>
    private static byte[] Close(byte[] record, int offset)
    {
        var span = record.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], 0xFFFF_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], (uint)(offset + 8));

        ApplyFixup(span);

        return record;
    }

    /// <summary>
    /// The exact inverse of <see cref="UpdateSequenceArray.TryApply"/>: displace the last two bytes
    /// of every sector into the array and stamp the sequence number in their place.
    /// </summary>
    private static void ApplyFixup(Span<byte> record)
    {
        const ushort Stamp = 0x5A5A;

        var array = record.Slice(UsaOffset, UsaCount * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(array, Stamp);

        for (var i = 0; i < UsaCount - 1; i++)
        {
            var tail = record.Slice(((i + 1) * BytesPerSector) - 2, 2);
            tail.CopyTo(array[((i + 1) * 2)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(tail, Stamp);
        }
    }
}
