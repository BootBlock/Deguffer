using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A synthetic Master File Table, assembled from a described tree.
///
/// This exists because reading a real MFT needs administrator rights (§6.3), so the alternative is
/// a scanner whose correctness is only ever checked on the maintainer's own elevated machine — and
/// a size scanner that is subtly wrong reports plausible numbers rather than failing, which §5.5's
/// whole design rests on not happening.
///
/// Each method here names an on-disk shape a real volume produces. What those shapes look like as
/// bytes is <see cref="MftRecordBytes"/>'s job.
/// </summary>
public sealed class MftFixture
{
    private readonly List<byte[]> _records = [];

    private long _unreadableFrom = long.MaxValue;

    public MftFixture()
    {
        // Records 0-4 are NTFS's own named metadata files ($MFT, $MFTMirr, $LogFile, $Volume,
        // $AttrDef), left blank here: an unused entry is skipped by the parser, which is worth
        // exercising rather than working around. Record 5 is the root.
        for (var i = 0; i < MftRecord.RootRecordNumber; i++)
        {
            _records.Add(new byte[MftRecordBytes.BytesPerRecord]);
        }

        // The root is its own parent — the shape the index has to detect to avoid a cyclic walk.
        _records.Add(MftRecordBytes.Build(
            MftRecord.RootRecordNumber,
            ".",
            isDirectory: true,
            MftRecordBytes.DirectoryStreamBytes,
            MftRecordBytes.DirectoryStreamBytes,
            DataPlacement.NonResident));

        // 6 to 11 are the rest of the named metadata, blank for the same reason as 0 to 4.
        while (_records.Count < 12)
        {
            _records.Add(new byte[MftRecordBytes.BytesPerRecord]);
        }

        // 12 to 15 are not blank on a real volume, and this is the whole point of filling them in.
        // NTFS holds them back for future metadata, marks them in use, and gives them neither a
        // $FILE_NAME nor an $ATTRIBUTE_LIST. Leaving them out of the fixture is what let a builder
        // that abandons the volume on that shape stay green through every test here while §5.5's
        // fast path could not engage on any real machine. A fake that models an idealised volume
        // proves the reader works on a volume nobody has.
        while (_records.Count < MftRecord.ReservedRecordCount)
        {
            _records.Add(MftRecordBytes.RecordWithoutAName(withAttributeList: false));
        }
    }

    /// <param name="created">
    /// When <c>$STANDARD_INFORMATION</c> says the directory was made. Null leaves the record's times
    /// at zero, which is what NTFS writes for a time it never set.
    /// </param>
    /// <param name="lastWritten">When its own entry was last altered. See the note on the parameter above.</param>
    public MftFixture AddDirectory(
        uint number, uint parent, string name, DateTime? created = null, DateTime? lastWritten = null) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent),
            name,
            isDirectory: true,
            MftRecordBytes.DirectoryStreamBytes,
            MftRecordBytes.DirectoryStreamBytes,
            DataPlacement.NonResident,
            reparseTag: 0,
            FileTime(created),
            FileTime(lastWritten)));

    /// <summary>
    /// A junction or directory symbolic link. Its target's entries belong to the target's own
    /// directory, so this one has no children in the table however much the path appears to hold.
    /// </summary>
    public MftFixture AddDirectoryLink(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent),
            name,
            isDirectory: true,
            MftRecordBytes.DirectoryStreamBytes,
            MftRecordBytes.DirectoryStreamBytes,
            DataPlacement.NonResident,
            MftRecordBytes.MountPointTag));

    /// <summary>
    /// A file that is a link rather than the thing it names — a symbolic link, or a placeholder a
    /// storage tier left behind. It declares a size and occupies none of it here, so a reader that
    /// counts it disagrees with a walk, which does not enter reparse points at all.
    /// </summary>
    public MftFixture AddFileLink(uint number, uint parent, string name, long logical) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical, DataPlacement.NonResident,
            MftRecordBytes.SymbolicLinkTag));

    /// <summary>
    /// A file compressed in place by the Windows Overlay Filter. It carries a reparse point and is
    /// not a link: the content is there, and a walk counts it because the filter hides the reparse
    /// attribute from an ordinary enumeration.
    /// </summary>
    public MftFixture AddOverlayCompressedFile(uint number, uint parent, string name, long allocated, long logical) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated, logical, DataPlacement.NonResident,
            MftRecordBytes.WindowsOverlayFilterTag));

    /// <summary>
    /// A file whose allocated and logical sizes may differ — the compressed or sparse case that a
    /// <c>FileInfo.Length</c> walk cannot see.
    /// </summary>
    public MftFixture AddFile(
        uint number,
        uint parent,
        string name,
        long allocated,
        long logical,
        DateTime? created = null,
        DateTime? lastWritten = null) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated, logical, DataPlacement.NonResident,
            reparseTag: 0, FileTime(created), FileTime(lastWritten)));

    /// <summary>
    /// A file whose record carries no <c>$STANDARD_INFORMATION</c>, so nothing can date it. It still
    /// has a name, a parent and a size, and it still has to place and draw —
    /// <see cref="MftRecordBytes.FileWithoutTimestamps"/> says why refusing it would be the wrong
    /// trade.
    /// </summary>
    public MftFixture AddFileWithNoTimestamps(uint number, uint parent, string name, long logical) =>
        Add(number, MftRecordBytes.FileWithoutTimestamps(Reference(parent), name, logical));

    /// <summary>
    /// A file small enough to live inside its own MFT record. It occupies no clusters, so deleting
    /// it frees no extents — allocated is genuinely zero.
    /// </summary>
    public MftFixture AddResidentFile(
        uint number,
        uint parent,
        string name,
        int length,
        DateTime? created = null,
        DateTime? lastWritten = null) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical: length, DataPlacement.Resident,
            reparseTag: 0, FileTime(created), FileTime(lastWritten)));

    /// <summary>
    /// A file with no unnamed <c>$DATA</c> at all, as a symbolic link has: its content is somewhere
    /// else entirely. Occupying nothing is the true answer here, and it has to stay distinguishable
    /// from a size the reader failed to establish.
    /// </summary>
    public MftFixture AddFileWithNoDataStream(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.NoData));

    /// <summary>
    /// A file whose <c>$DATA</c> no longer fits in its base record. NTFS moves the attribute into an
    /// extension record and leaves an <c>$ATTRIBUTE_LIST</c> behind pointing at it, so the base
    /// record carries a name and no size at all — which is not the same as a size of zero.
    /// </summary>
    public MftFixture AddFileWithDataInAnExtensionRecord(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.InExtensionRecord));

    /// <summary>
    /// A file fragmented across extents but still fully described here: an attribute list, then the
    /// extent starting at VCN 0 that carries the sizes, then a continuation extent that does not.
    /// The sizes are known, so this must not be confused with a record that has lost them.
    /// </summary>
    public MftFixture AddFileSplitAcrossExtents(uint number, uint parent, string name, long allocated, long logical) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated, logical, DataPlacement.SplitAcrossExtents));

    /// <summary>
    /// A file whose base record holds a later extent of a split <c>$DATA</c> rather than the first.
    /// Only the extent starting at VCN 0 carries the sizes; the rest leave those fields zero, so a
    /// reader that trusts them reads a real file as empty.
    /// </summary>
    public MftFixture AddFileDescribingOnlyALaterExtent(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.LaterExtent));

    /// <summary>
    /// A non-resident <c>$DATA</c> whose declared length stops before the size fields — a corrupt
    /// record, and one whose sizes cannot be read rather than being zero.
    /// </summary>
    public MftFixture AddFileWithATruncatedDataHeader(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.TruncatedHeader));

    /// <summary>The same corruption in a resident <c>$DATA</c>, where the length field itself is cut off.</summary>
    public MftFixture AddFileWithATruncatedResidentDataHeader(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.TruncatedResidentHeader));

    /// <summary>
    /// A directory big enough that NTFS moved its index attributes out of the base record. Common
    /// on any real volume, and carrying no size of its own that anything counts.
    /// </summary>
    public MftFixture AddDirectoryWithAttributesInAnExtensionRecord(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: true, allocated: 0, logical: 0, DataPlacement.InExtensionRecord));

    /// <summary>
    /// One of the extension records the shapes above point at. A real volume holds many, and none
    /// of them is a fault: the base record that owns them carries the file's identity.
    /// </summary>
    public MftFixture AddExtensionRecord(uint number, uint baseRecordNumber) =>
        Add(number, MftRecordBytes.ExtensionRecord(baseRecordNumber));

    /// <summary>
    /// A base record whose names live in extension records, which is what NTFS does once a file has
    /// enough hard links to overflow its own record. A system volume is full of these, so a reader
    /// that treats one as corruption gives up on the volume that matters most.
    /// </summary>
    public MftFixture AddRecordWithNamesInExtensionRecords(uint number) =>
        Add(number, MftRecordBytes.RecordWithoutAName(withAttributeList: true));

    /// <summary>
    /// The same shape without the attribute list: a record in use, holding data, claiming no
    /// identity and pointing nowhere else for one. No healthy volume produces this.
    /// </summary>
    public MftFixture AddRecordWithNoIdentityAtAll(uint number) =>
        Add(number, MftRecordBytes.RecordWithoutAName(withAttributeList: false));

    /// <summary>
    /// A record naming a parent beyond the 32-bit range the index addresses. Narrowing this
    /// silently would wrap it onto an unrelated record and graft a subtree somewhere it never was.
    /// </summary>
    public MftFixture AddFileWithUnaddressableParent(uint number, string name, long allocated) =>
        Add(number, MftRecordBytes.Build(
            0x1_0000_0007UL | (1UL << 48), name, isDirectory: false, allocated, allocated, DataPlacement.NonResident));

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
        var name = new string('n', MftRecordBytes.NameLengthPuttingSizeFieldAcrossBoundary());

        return Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated, logical, DataPlacement.NonResident));
    }

    /// <summary>Blank out a record, standing in for a free or never-used entry.</summary>
    public MftFixture AddUnused(uint number) => Add(number, new byte[MftRecordBytes.BytesPerRecord]);

    /// <summary>
    /// Break one sector's update sequence stamp, as a torn write would. The record must then be
    /// rejected outright — a half-fixed-up record parses cleanly and reports a wrong size.
    /// </summary>
    public MftFixture CorruptSectorStamp(uint number)
    {
        _records[(int)number][MftRecordBytes.BytesPerSector - 1] ^= 0xFF;
        return this;
    }

    /// <summary>
    /// Make reads fail from <paramref name="record"/> onward, as a bad sector or a run list the
    /// reader could not follow would. The index must refuse rather than total what it did get.
    /// </summary>
    public MftFixture UnreadableFrom(long record)
    {
        _unreadableFrom = record;
        return this;
    }

    public IMftSource Build() =>
        new FixtureMftSource(_records, MftRecordBytes.BytesPerSector, MftRecordBytes.BytesPerRecord, _unreadableFrom);

    /// <summary>
    /// A parent as NTFS stores it: record number in the low 48 bits, reuse sequence above. The
    /// sequence is deliberately non-zero, because a reader that forgets to mask it off still works
    /// on a freshly formatted volume and fails on a used one.
    /// </summary>
    private static ulong Reference(uint recordNumber) => recordNumber | (1UL << 48);

    /// <summary>
    /// A <see cref="DateTime"/> as NTFS stores one, or zero for "never set".
    ///
    /// <para>Converted through <see cref="DateTime.ToFileTimeUtc"/> rather than by arithmetic here,
    /// so a fixture and the reader under test are not two copies of the same epoch calculation
    /// agreeing with each other about a mistake.</para>
    /// </summary>
    private static long FileTime(DateTime? when) => when?.ToFileTimeUtc() ?? 0;

    private MftFixture Add(uint number, byte[] record)
    {
        while (_records.Count <= number)
        {
            _records.Add(new byte[MftRecordBytes.BytesPerRecord]);
        }

        _records[(int)number] = record;
        return this;
    }
}
