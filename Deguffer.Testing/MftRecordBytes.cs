using System.Buffers.Binary;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>Writes one attribute at the start of <paramref name="target"/>, returning its length.</summary>
internal delegate int AttributeWriter(Span<byte> target);

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
    /// What a non-resident attribute list's runs count in. Larger than a record, as on a real
    /// volume, so a reader that confuses clusters with records reads the wrong bytes.
    /// </summary>
    public const int BytesPerCluster = 4096;

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
        var offset = WriteHeader(span, (ushort)(isDirectory ? 0x0003 : 0x0001), baseReference: 0, Sequence);

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
    /// A record holding exactly <paramref name="attributes"/>, in order — the shape every record that
    /// spreads a file across several takes, base and extension alike.
    /// </summary>
    /// <param name="baseReference">
    /// Zero for a base record. For an extension record, the base record that owns it, as a file
    /// reference: number, then sequence above.
    /// </param>
    /// <param name="sequence">How many times the record has been reused.</param>
    public static byte[] Compose(
        bool isDirectory, ulong baseReference, ushort sequence, params AttributeWriter[] attributes)
    {
        var record = new byte[BytesPerRecord];
        var span = record.AsSpan();
        var offset = WriteHeader(span, (ushort)(isDirectory ? 0x0003 : 0x0001), baseReference, sequence);

        foreach (var write in attributes)
        {
            offset += write(span[offset..]);
        }

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
        var offset = WriteHeader(span, flags: 0x0001, baseReference: 0, Sequence);

        offset += MftAttributeBytes.WriteFileName(span[offset..], parentReference, name, logical, logical);
        offset += MftAttributeBytes.WriteData(span[offset..], logical, logical, DataPlacement.NonResident);

        return Close(record, offset);
    }

    /// <summary>
    /// A record in use carrying no <c>$FILE_NAME</c> and no <c>$ATTRIBUTE_LIST</c> to say where one
    /// went: NTFS's reserved records 12 to 15, and damage anywhere else.
    /// </summary>
    public static byte[] RecordWithoutAName()
    {
        var record = new byte[BytesPerRecord];
        var span = record.AsSpan();
        var offset = WriteHeader(span, flags: 0x0001, baseReference: 0, Sequence);

        // Dated like any other record. This shape stands in for reserved records 12 to 15 among
        // others, and those carry times on a real volume — the thing that makes them unreadable is
        // the missing $FILE_NAME, not a missing $STANDARD_INFORMATION. Leaving it out here would
        // have made the four records the reader's carve-out exists for the least realistic ones in
        // the fixture.
        offset += MftAttributeBytes.WriteStandardInformation(span[offset..], created: 0, lastWritten: 0);
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
        var offset = WriteHeader(span, flags: 0x0001, baseReference: 0, Sequence);

        // Record 0 is a real file with real times, so it carries the attribute like the rest. The
        // run list that follows is located by an offset within its own attribute, so starting it
        // further into the record changes nothing about how it is read.
        offset += MftAttributeBytes.WriteStandardInformation(span[offset..], created: 0, lastWritten: 0);

        if (withAttributeList)
        {
            ulong self = (ulong)Sequence << 48;

            offset += MftAttributeBytes.WriteAttributeList(
                span[offset..],
                [new ListedAttribute(0x10, self), new ListedAttribute(0x80, self)]);
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

    /// <summary>
    /// The sequence number every fixture record carries unless a test asks for another. Not zero,
    /// for the reason <see cref="MftFixture"/>'s references are not: a reader that ignores it still
    /// works on a freshly formatted volume.
    /// </summary>
    public const ushort Sequence = 1;

    /// <summary>
    /// A corrupt length chosen where it does the most damage: short of <see cref="int.MaxValue"/> by
    /// less than any offset it is added to, so a bounds check that adds the two wraps negative and
    /// passes, and the slice after it throws. Every offset the reader adds a length to is above 1.
    /// </summary>
    public const uint LengthJustUnderIntMax = int.MaxValue - 1;

    /// <summary>
    /// Overwrite the length the first attribute of a record declares — <c>$STANDARD_INFORMATION</c>
    /// in a record <see cref="Build"/> or <see cref="SelfRecord"/> writes, though any first attribute
    /// serves. The field lies well before the first sector stamp, so the
    /// record's fixup still holds and the corruption is the only thing wrong with it.
    /// </summary>
    public static void DeclareFirstAttributeLength(Span<byte> record, uint length) =>
        BinaryPrimitives.WriteUInt32LittleEndian(record[(FirstAttributeOffset() + 0x04)..], length);

    /// <summary>
    /// Overwrite the value length a record's <c>$FILE_NAME</c> declares, leaving the attribute's own
    /// length intact so the walk reaches it. It follows <c>$STANDARD_INFORMATION</c> in every record
    /// <see cref="Build"/> writes.
    /// </summary>
    public static void DeclareFileNameValueLength(Span<byte> record, uint length) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            record[(FirstAttributeOffset() + MftAttributeBytes.StandardInformationLength + 0x10)..],
            length);

    private static int UsaCount => (BytesPerRecord / BytesPerSector) + 1;

    private static int FirstAttributeOffset() => MftAttributeBytes.Align8(UsaOffset + (UsaCount * 2));

    /// <summary>The fixed part every record starts with. Returns the offset its attributes begin at.</summary>
    private static int WriteHeader(Span<byte> record, ushort flags, ulong baseReference, ushort sequence)
    {
        var firstAttribute = FirstAttributeOffset();

        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x04..], UsaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x06..], (ushort)UsaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x10..], sequence);
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
