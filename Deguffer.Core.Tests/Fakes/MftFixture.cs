using System.Buffers.Binary;
using System.Text;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Builds a synthetic Master File Table, byte for byte, from a described tree.
///
/// This exists because reading a real MFT needs administrator rights (§6.3), so the alternative is
/// a scanner whose correctness is only ever checked on the maintainer's own elevated machine — and
/// a size scanner that is subtly wrong reports plausible numbers rather than failing, which §5.5's
/// whole design rests on not happening.
///
/// The builder is a genuine inverse of the reader rather than a stub: it lays out real attribute
/// records and applies a real update sequence array, so a test that measures a fixture tree is
/// exercising the same parsing that runs against a live volume.
/// </summary>
public sealed class MftFixture
{
    private const int BytesPerSector = 512;
    private const int BytesPerRecord = 1024;

    private readonly List<byte[]> _records = [];

    public MftFixture()
    {
        // Records 0-4 are NTFS's own metadata files ($MFT, $MFTMirr, $LogFile, $Volume, $AttrDef)
        // and are left blank here: unused entries are skipped by the parser, which is itself worth
        // exercising rather than working around. Record 5 is the root, so fixtures number their
        // own entries from 6.
        for (var i = 0; i < MftRecord.RootRecordNumber; i++)
        {
            _records.Add(new byte[BytesPerRecord]);
        }

        // The root is its own parent — the shape the index has to detect to avoid a cyclic walk.
        _records.Add(BuildRecord(
            MftRecord.RootRecordNumber, ".", isDirectory: true, DirectoryStreamBytes, DirectoryStreamBytes, DataPlacement.NonResident));
    }

    /// <summary>
    /// Every fixture directory carries a non-zero <c>$DATA</c> stream, and it is deliberately not
    /// zero: a reader that counted a directory's own data would double every file beneath it, and
    /// against zero-sized directory streams that bug is invisible.
    /// </summary>
    public const long DirectoryStreamBytes = 512;

    public MftFixture AddDirectory(uint number, uint parent, string name) =>
        Add(number, BuildRecord(Reference(parent), name, isDirectory: true, DirectoryStreamBytes, DirectoryStreamBytes, DataPlacement.NonResident));

    /// <summary>
    /// A parent as NTFS stores it: record number in the low 48 bits, reuse sequence above. The
    /// sequence is deliberately non-zero, because a reader that forgets to mask it off still works
    /// on a freshly formatted volume and fails on a used one.
    /// </summary>
    private static ulong Reference(uint recordNumber) => recordNumber | (1UL << 48);

    /// <summary>
    /// A record naming a parent beyond the 32-bit range the index addresses. Narrowing this
    /// silently would wrap it onto an unrelated record and graft a subtree somewhere it never was.
    /// </summary>
    public MftFixture AddFileWithUnaddressableParent(uint number, string name, long allocated) =>
        Add(number, BuildRecord(0x1_0000_0007UL | (1UL << 48), name, isDirectory: false, allocated, allocated, DataPlacement.NonResident));

    /// <summary>
    /// A file whose allocated and logical sizes may differ — the compressed or sparse case that a
    /// <c>FileInfo.Length</c> walk cannot see.
    /// </summary>
    public MftFixture AddFile(uint number, uint parent, string name, long allocated, long logical) =>
        Add(number, BuildRecord(Reference(parent), name, isDirectory: false, allocated, logical, DataPlacement.NonResident));

    /// <summary>
    /// A file small enough to live inside its own MFT record. It occupies no clusters, so deleting
    /// it frees no extents — allocated is genuinely zero.
    /// </summary>
    public MftFixture AddResidentFile(uint number, uint parent, string name, int length) =>
        Add(number, BuildRecord(Reference(parent), name, isDirectory: false, allocated: 0, logical: length, DataPlacement.Resident));

    /// <summary>
    /// A file whose <c>$DATA</c> no longer fits in its base record. NTFS moves the attribute into an
    /// extension record and leaves an <c>$ATTRIBUTE_LIST</c> behind pointing at it, so the base
    /// record carries a name and no size at all — which is not the same as a size of zero.
    /// </summary>
    public MftFixture AddFileWithDataInAnExtensionRecord(uint number, uint parent, string name) =>
        Add(number, BuildRecord(Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.InExtensionRecord));

    /// <summary>
    /// A file whose base record holds a later extent of a split <c>$DATA</c> rather than the first.
    /// Only the extent starting at VCN 0 carries the sizes; the rest leave those fields zero, so a
    /// reader that trusts them reads a real file as empty.
    /// </summary>
    public MftFixture AddFileDescribingOnlyALaterExtent(uint number, uint parent, string name) =>
        Add(number, BuildRecord(Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.LaterExtent));

    /// <summary>
    /// A non-resident <c>$DATA</c> whose declared length stops before the size fields — a corrupt
    /// record, and one whose sizes cannot be read rather than being zero.
    /// </summary>
    public MftFixture AddFileWithATruncatedDataHeader(uint number, uint parent, string name) =>
        Add(number, BuildRecord(Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.TruncatedHeader));

    /// <summary>
    /// A file with no unnamed <c>$DATA</c> at all, as a symbolic link has: its content is somewhere
    /// else entirely. Occupying nothing is the true answer here, and it has to stay distinguishable
    /// from a size the reader failed to establish.
    /// </summary>
    public MftFixture AddFileWithNoDataStream(uint number, uint parent, string name) =>
        Add(number, BuildRecord(Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.NoData));

    /// <summary>
    /// A directory big enough that NTFS moved its index attributes out of the base record. Common
    /// on any real volume, and carrying no size of its own that anything counts.
    /// </summary>
    public MftFixture AddDirectoryWithAttributesInAnExtensionRecord(uint number, uint parent, string name) =>
        Add(number, BuildRecord(Reference(parent), name, isDirectory: true, allocated: 0, logical: 0, DataPlacement.InExtensionRecord));

    /// <summary>Where a record keeps the bytes of its unnamed <c>$DATA</c>, and whether they can be read.</summary>
    private enum DataPlacement
    {
        NonResident,
        Resident,
        NoData,
        InExtensionRecord,
        LaterExtent,
        TruncatedHeader,
    }

    /// <summary>
    /// A file whose name is sized so that its <c>$DATA</c> allocated-size field lies across the
    /// first sector boundary, and so is one of the fields NTFS displaces into the update sequence
    /// array.
    ///
    /// Without a record shaped like this the fixup is untested: short records leave the boundary
    /// sitting in trailing zeroes, where failing to restore the displaced bytes changes nothing.
    /// On a real volume the boundary lands in live attribute data, and two unrestored bytes inside
    /// a 64-bit size field alter it by up to 2^48 — a wrong number, reported confidently.
    /// </summary>
    public MftFixture AddFileWithSizeAcrossSectorBoundary(uint number, uint parent, long allocated, long logical)
    {
        var name = new string('n', NameLengthPuttingSizeFieldAcrossBoundary());
        return Add(number, BuildRecord(Reference(parent), name, isDirectory: false, allocated, logical, DataPlacement.NonResident));
    }

    /// <summary>
    /// Solve for the name length that pushes <c>$DATA</c>'s allocated field over byte 510. Derived
    /// rather than hard-coded so it stays correct if the record layout above is ever adjusted.
    /// </summary>
    private static int NameLengthPuttingSizeFieldAcrossBoundary()
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

    /// <summary>Blank out a record, standing in for a free or unreadable entry.</summary>
    public MftFixture AddUnused(uint number) => Add(number, new byte[BytesPerRecord]);

    /// <summary>
    /// Break one sector's update sequence stamp, as a torn write would. The record must then be
    /// rejected outright — a half-fixed-up record parses cleanly and reports a wrong size.
    /// </summary>
    public MftFixture CorruptSectorStamp(uint number)
    {
        var record = _records[(int)number];
        record[BytesPerSector - 1] ^= 0xFF;
        return this;
    }

    private MftFixture Add(uint number, byte[] record)
    {
        while (_records.Count <= number)
        {
            _records.Add(new byte[BytesPerRecord]);
        }

        _records[(int)number] = record;
        return this;
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
        const int UsaOffset = 0x30;
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

        ApplyFixup(span, UsaOffset, usaCount);
        return record;
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

    private long _unreadableFrom = long.MaxValue;

    /// <summary>
    /// Make reads fail from <paramref name="record"/> onward, as a bad sector or a run list the
    /// reader could not follow would. The index must refuse rather than total what it did get.
    /// </summary>
    public MftFixture UnreadableFrom(long record)
    {
        _unreadableFrom = record;
        return this;
    }

    public IMftSource Build() => new FixtureMftSource(_records, BytesPerSector, BytesPerRecord, _unreadableFrom);

    private static byte[] BuildRecord(
        ulong parentReference,
        string name,
        bool isDirectory,
        long allocated,
        long logical,
        DataPlacement placement)
    {
        var record = new byte[BytesPerRecord];
        var span = record.AsSpan();

        var usaCount = (BytesPerRecord / BytesPerSector) + 1;
        const int UsaOffset = 0x30;
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
        offset += WriteFileName(span[offset..], parentReference, name, allocated, logical);
        offset += placement switch
        {
            DataPlacement.Resident => WriteResidentData(span[offset..], (int)logical),
            DataPlacement.NoData => 0,
            DataPlacement.InExtensionRecord => WriteAttributeList(span[offset..]),
            DataPlacement.LaterExtent => WriteNonResidentData(span[offset..], allocated, logical, startVirtualCluster: 4),
            DataPlacement.TruncatedHeader => WriteTruncatedData(span[offset..]),
            _ => WriteNonResidentData(span[offset..], allocated, logical, startVirtualCluster: 0),
        };

        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], 0xFFFF_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], (uint)(offset + 8));

        ApplyFixup(span, UsaOffset, usaCount);
        return record;
    }

    /// <summary>
    /// The exact inverse of <see cref="UpdateSequenceArray.TryApply"/>: displace the last two bytes
    /// of every sector into the array and stamp the sequence number in their place.
    /// </summary>
    private static void ApplyFixup(Span<byte> record, int usaOffset, int usaCount)
    {
        const ushort Stamp = 0x5A5A;

        var array = record.Slice(usaOffset, usaCount * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(array, Stamp);

        for (var i = 0; i < usaCount - 1; i++)
        {
            var tail = record.Slice(((i + 1) * BytesPerSector) - 2, 2);
            tail.CopyTo(array[((i + 1) * 2)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(tail, Stamp);
        }
    }

    private static int WriteFileName(Span<byte> target, ulong parentReference, string name, long allocated, long logical)
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

    /// <summary>A non-resident <c>$DATA</c> whose declared length stops before its size fields.</summary>
    private static int WriteTruncatedData(Span<byte> target)
    {
        const int Length = 0x30;

        BinaryPrimitives.WriteUInt32LittleEndian(target, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x04..], Length);
        target[0x08] = 1;

        return Length;
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

    private static int Align8(int value) => (value + 7) & ~7;
}
