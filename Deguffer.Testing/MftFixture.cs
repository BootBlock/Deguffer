using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

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
    private const uint StandardInformation = 0x10;
    private const uint FileName = 0x30;
    private const uint Data = 0x80;
    private const uint IndexAllocation = 0xA0;

    private readonly List<byte[]> _records = [];

    private readonly Dictionary<long, byte[]> _clusters = [];

    private static readonly byte[] Blank = new byte[MftRecordBytes.BytesPerRecord];

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
            _records.Add(MftRecordBytes.RecordWithoutAName());
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
    /// A file whose <c>$DATA</c> no longer fits in its base record. NTFS moves the attribute into
    /// <paramref name="extension"/> and leaves an <c>$ATTRIBUTE_LIST</c> behind pointing at it, so
    /// the base record carries a name and no size at all — which is not the same as a size of zero.
    /// The shape 400 of 400 records the index declined on a real volume took.
    /// </summary>
    /// <param name="mismatch">
    /// Where given, the list and the extension record disagree in this way, as they do for a file
    /// caught mid-change. A reader that takes the sizes anyway reports a file that has since become
    /// something else.
    /// </param>
    public MftFixture AddFileWithDataInAnExtensionRecord(
        uint number, uint parent, string name, long allocated, long logical, uint extension, ListMismatch? mismatch = null)
    {
        var (self, listed) = PlaceExtension(
            number, extension, mismatch, t => MftAttributeBytes.WriteNonResidentData(t, allocated, logical, startVirtualCluster: 0));

        return Add(number, ListingFile(self, parent, name, [new ListedAttribute(Data, listed)]));
    }

    /// <summary>
    /// The same, with the list itself grown too large for the base record. NTFS then keeps it in
    /// clusters outside the table, at <paramref name="listCluster"/> here, so following it takes a
    /// read the table's records cannot serve.
    /// </summary>
    public MftFixture AddFileWithANonResidentAttributeList(
        uint number, uint parent, string name, long allocated, long logical, uint extension, long listCluster) =>
        AddFileWithAListInClusters(number, parent, name, allocated, logical, extension, listCluster, listCluster);

    /// <summary>
    /// The same, with the list's one run sparse: it has a length and no place on the disk, so it
    /// holds no entries at all. A complete list is placed at cluster 0 as well, so a reader that
    /// took the sparse run's start of zero as a real cluster would find one and follow it.
    /// </summary>
    public MftFixture AddFileWithASparseAttributeList(
        uint number, uint parent, string name, long allocated, long logical, uint extension) =>
        AddFileWithAListInClusters(number, parent, name, allocated, logical, extension, listCluster: null, placedAt: 0);

    /// <summary>
    /// A file fragmented across two extension records. Only the piece starting at cluster 0 states
    /// the sizes, and here it sits in the later record: the earlier one holds a continuation whose
    /// size fields are zero, as NTFS leaves them.
    /// </summary>
    public MftFixture AddFileWithDataSplitAcrossExtensionRecords(
        uint number, uint parent, string name, long allocated, long logical, uint continuation, uint start) =>
        Add(number, ListingFile(Reference(number), parent, name,
            [
                new ListedAttribute(Data, Reference(start), LowestVcn: 0),
                new ListedAttribute(Data, Reference(continuation), LowestVcn: 4),
            ]))
            .Add(continuation, DataPiece(number, allocated: 0, logical: 0, startVirtualCluster: 4))
            .Add(start, DataPiece(number, allocated, logical, startVirtualCluster: 0));

    /// <summary>
    /// A file whose name moved into <paramref name="extension"/>, which is what NTFS does once a
    /// file has enough hard links to overflow its own record. A system volume is full of these, and
    /// the base record alone cannot say which directory the file is in.
    /// </summary>
    /// <param name="mismatch">
    /// Where given, the list and the extension record disagree in this way. Nothing then says which
    /// directory the file is in, so it could belong to any.
    /// </param>
    public MftFixture AddFileWithItsNameInAnExtensionRecord(
        uint number, uint parent, string name, long allocated, long logical, uint extension, ListMismatch? mismatch = null)
    {
        var (self, listed) = PlaceExtension(
            number, extension, mismatch, t => MftAttributeBytes.WriteFileName(t, Reference(parent), name, allocated, logical));

        return Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteAttributeList(t,
            [
                new ListedAttribute(StandardInformation, self),
                new ListedAttribute(FileName, listed),
                new ListedAttribute(Data, self),
            ]),
            t => MftAttributeBytes.WriteNonResidentData(t, allocated, logical, startVirtualCluster: 0)));
    }

    /// <summary>
    /// A file whose base record keeps only its 8.3 alias, under <paramref name="aliasParent"/>,
    /// while its Win32 name — a hard link in another directory — moved to
    /// <paramref name="extension"/>. The Win32 name outranks the alias wherever it is kept, so the
    /// file belongs under <paramref name="parent"/>.
    /// </summary>
    /// <param name="mismatch">
    /// Where given, the list and the extension record disagree in this way. The alias is then the
    /// only name left, and placing the file by it would be a guess at its directory.
    /// </param>
    public MftFixture AddFileWithItsBetterNameInAnExtensionRecord(
        uint number,
        uint aliasParent,
        string alias,
        uint parent,
        string name,
        long logical,
        uint extension,
        ListMismatch? mismatch = null) =>
        AddFileWithANameInAnExtensionRecord(
            number,
            t => MftAttributeBytes.WriteFileName(t, Reference(aliasParent), alias, logical, logical, nameSpace: 2),
            t => MftAttributeBytes.WriteFileName(t, Reference(parent), name, logical, logical, nameSpace: 1),
            logical,
            extension,
            mismatch);

    /// <summary>
    /// A file with two hard links: its own record keeps the one under <paramref name="parent"/>,
    /// and the one under <paramref name="otherParent"/> moved to <paramref name="extension"/>. Both
    /// are in the best namespace, so nothing in the extension record can outrank the name the base
    /// record already holds.
    /// </summary>
    public MftFixture AddHardLinkedFileWithItsOtherNameInAnExtensionRecord(
        uint number,
        uint parent,
        string name,
        uint otherParent,
        string otherName,
        long logical,
        uint extension,
        ListMismatch? mismatch = null) =>
        AddFileWithANameInAnExtensionRecord(
            number,
            t => MftAttributeBytes.WriteFileName(t, Reference(parent), name, logical, logical),
            t => MftAttributeBytes.WriteFileName(t, Reference(otherParent), otherName, logical, logical),
            logical,
            extension,
            mismatch);

    /// <summary>
    /// A file whose <c>$ATTRIBUTE_LIST</c> cannot be read: its one entry declares a length that
    /// runs past the end of the list. A list read in part may have lost the very entry saying
    /// where the size went.
    /// </summary>
    public MftFixture AddFileWithAMalformedAttributeList(uint number, uint parent, string name)
    {
        var value = MftAttributeBytes.AttributeListValue([new ListedAttribute(Data, Reference(number))]);
        value[0x04] = 0xFF;

        return Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteFileName(t, Reference(parent), name, 0, 0),
            t => MftAttributeBytes.WriteAttributeListValue(t, value)));
    }

    /// <summary>
    /// A file fragmented across extents but still fully described here: an attribute list naming
    /// this record for every piece, then the extent starting at VCN 0 that carries the sizes, then
    /// a continuation extent that does not. The sizes are known, so this must not be confused with
    /// a record that has lost them.
    /// </summary>
    public MftFixture AddFileSplitAcrossExtents(uint number, uint parent, string name, long allocated, long logical) =>
        Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteFileName(t, Reference(parent), name, allocated, logical),
            t => MftAttributeBytes.WriteAttributeList(t,
            [
                new ListedAttribute(StandardInformation, Reference(number)),
                new ListedAttribute(FileName, Reference(number)),
                new ListedAttribute(Data, Reference(number), LowestVcn: 0),
                new ListedAttribute(Data, Reference(number), LowestVcn: 4),
            ]),
            t => MftAttributeBytes.WriteNonResidentData(t, allocated, logical, startVirtualCluster: 0),
            t => MftAttributeBytes.WriteNonResidentData(t, allocated: 0, logical: 0, startVirtualCluster: 4)));

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
    /// A directory big enough that NTFS moved its index into <paramref name="extension"/>. Common on
    /// any real volume, and carrying no size of its own that anything counts, so nothing in the
    /// extension record is needed to measure it.
    /// </summary>
    public MftFixture AddDirectoryWithAttributesInAnExtensionRecord(uint number, uint parent, string name, uint extension) =>
        Add(number, MftRecordBytes.Compose(
                isDirectory: true,
                baseReference: 0,
                MftRecordBytes.Sequence,
                t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
                t => MftAttributeBytes.WriteFileName(t, Reference(parent), name, 0, 0),
                t => MftAttributeBytes.WriteAttributeList(t,
                [
                    new ListedAttribute(StandardInformation, Reference(number)),
                    new ListedAttribute(FileName, Reference(number)),
                    new ListedAttribute(IndexAllocation, Reference(extension), Name: "$I30"),
                ])))
            .Add(extension, MftRecordBytes.Compose(isDirectory: true, Reference(number), MftRecordBytes.Sequence));

    /// <summary>
    /// An extension record met on its own in the first pass. A real volume holds many, and none of
    /// them is a fault: the base record that owns it carries the file's identity.
    /// </summary>
    public MftFixture AddExtensionRecord(uint number, uint baseRecordNumber) =>
        Add(number, MftRecordBytes.Compose(isDirectory: false, Reference(baseRecordNumber), MftRecordBytes.Sequence));

    /// <summary>
    /// A record in use, holding data, claiming no identity and with no attribute list pointing
    /// anywhere else for one. No healthy volume produces this.
    /// </summary>
    public MftFixture AddRecordWithNoIdentityAtAll(uint number) =>
        Add(number, MftRecordBytes.RecordWithoutAName());

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
        var record = (byte[])_records[(int)number].Clone();
        record[MftRecordBytes.BytesPerSector - 1] ^= 0xFF;
        _records[(int)number] = record;
        return this;
    }

    /// <summary>
    /// Make a record's first attribute declare itself far longer than the record, as a corrupt
    /// sector the stamps do not cover would. The record must be rejected, not thrown on.
    /// </summary>
    public MftFixture CorruptAttributeLength(uint number)
    {
        MftRecordBytes.DeclareFirstAttributeLength(_records[(int)number], MftRecordBytes.LengthJustUnderIntMax);
        return this;
    }

    /// <summary>
    /// Make a record's <c>$FILE_NAME</c> declare a value far longer than the attribute around it.
    /// The record then has no name it can be placed by, and must be rejected, not thrown on.
    /// </summary>
    public MftFixture CorruptFileNameValueLength(uint number)
    {
        MftRecordBytes.DeclareFileNameValueLength(_records[(int)number], MftRecordBytes.LengthJustUnderIntMax);
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
        new FixtureMftSource(
            _records,
            MftRecordBytes.BytesPerSector,
            MftRecordBytes.BytesPerRecord,
            _unreadableFrom,
            MftRecordBytes.BytesPerCluster,
            _clusters);

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

    /// <param name="listCluster">Where the base record says the list is, or null for a sparse run.</param>
    /// <param name="placedAt">Where the list's bytes actually are.</param>
    private MftFixture AddFileWithAListInClusters(
        uint number, uint parent, string name, long allocated, long logical, uint extension, long? listCluster, long placedAt)
    {
        var value = MftAttributeBytes.AttributeListValue(ListOf(Reference(number), [new ListedAttribute(Data, Reference(extension))]));
        var cluster = new byte[MftRecordBytes.BytesPerCluster];
        value.CopyTo(cluster, 0);
        _clusters[placedAt] = cluster;

        return Add(number, MftRecordBytes.Compose(
                isDirectory: false,
                baseReference: 0,
                MftRecordBytes.Sequence,
                t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
                t => MftAttributeBytes.WriteFileName(t, Reference(parent), name, 0, 0),
                t => MftAttributeBytes.WriteNonResidentAttributeList(t, listCluster, clusterCount: 1, value.Length)))
            .Add(extension, DataPiece(number, allocated, logical, startVirtualCluster: 0));
    }

    /// <summary>
    /// A file whose base record keeps one name, written by <paramref name="ownName"/>, and whose
    /// other name, written by <paramref name="otherName"/>, is in <paramref name="extension"/>.
    /// </summary>
    private MftFixture AddFileWithANameInAnExtensionRecord(
        uint number,
        AttributeWriter ownName,
        AttributeWriter otherName,
        long logical,
        uint extension,
        ListMismatch? mismatch)
    {
        var (self, listed) = PlaceExtension(number, extension, mismatch, otherName);

        return Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            ownName,
            t => MftAttributeBytes.WriteAttributeList(t,
            [
                new ListedAttribute(StandardInformation, self),
                new ListedAttribute(FileName, self),
                new ListedAttribute(FileName, listed),
                new ListedAttribute(Data, self),
            ]),
            t => MftAttributeBytes.WriteNonResidentData(t, logical, logical, startVirtualCluster: 0)));
    }

    /// <summary>
    /// Put <paramref name="content"/> in extension record <paramref name="extension"/>, owned by
    /// record <paramref name="number"/> and wrong in the way <paramref name="mismatch"/> names, if
    /// one is given. Returns how the owner's list names its own record and the extension record.
    /// </summary>
    private (ulong Self, ulong Listed) PlaceExtension(
        uint number, uint extension, ListMismatch? mismatch, AttributeWriter content)
    {
        // A record number no fixture table reaches, so a read of it finds the end of the table.
        const uint PastTheTable = 0x00FF_FFFF;

        var stale = (ushort)(MftRecordBytes.Sequence + 1);
        var self = Reference(number);
        var owner = Reference(number);
        var sequence = MftRecordBytes.Sequence;
        AttributeWriter[] attributes = [content];

        switch (mismatch)
        {
            case ListMismatch.ItsOwnSequence:
                sequence = stale;
                break;

            case ListMismatch.OwnerNumber:
                owner = Reference(number + 1);
                break;

            case ListMismatch.OwnerSequence:
                owner = number | ((ulong)stale << 48);
                break;

            case ListMismatch.HoldsNothingListed:
                attributes = [];
                break;

            case ListMismatch.OutsideTheTable:
                return (self, Reference(PastTheTable));

            case ListMismatch.ListNamesItsOwnRecordAsItWas:
                self = number | ((ulong)stale << 48);
                break;
        }

        Add(extension, MftRecordBytes.Compose(isDirectory: false, owner, sequence, attributes));
        return (self, Reference(extension));
    }

    /// <summary>
    /// A base record keeping its name and its dates, with a list naming the record as
    /// <paramref name="self"/> for those and <paramref name="elsewhere"/> for the rest.
    /// </summary>
    private static byte[] ListingFile(ulong self, uint parent, string name, IReadOnlyList<ListedAttribute> elsewhere) =>
        MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteFileName(t, Reference(parent), name, 0, 0),
            t => MftAttributeBytes.WriteAttributeList(t, ListOf(self, elsewhere)));

    private static IReadOnlyList<ListedAttribute> ListOf(ulong self, IReadOnlyList<ListedAttribute> elsewhere) =>
    [
        new ListedAttribute(StandardInformation, self),
        new ListedAttribute(FileName, self),
        .. elsewhere,
    ];

    /// <summary>An extension record holding one piece of <paramref name="owner"/>'s unnamed <c>$DATA</c>.</summary>
    private static byte[] DataPiece(uint owner, long allocated, long logical, long startVirtualCluster) =>
        MftRecordBytes.Compose(
            isDirectory: false,
            Reference(owner),
            MftRecordBytes.Sequence,
            t => MftAttributeBytes.WriteNonResidentData(t, allocated, logical, startVirtualCluster));

    private MftFixture Add(uint number, byte[] record)
    {
        // Every gap is the one blank record, shared. A source copies what it serves and nothing
        // writes to a record in place without copying it first, so a test can place a record past
        // a hundred thousand others for the cost of the references.
        while (_records.Count <= number)
        {
            _records.Add(Blank);
        }

        _records[(int)number] = record;
        return this;
    }
}
