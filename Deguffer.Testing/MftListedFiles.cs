using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>
/// Files NTFS spreads across several records: a base record whose <c>$ATTRIBUTE_LIST</c> says which
/// extension record each of its attributes moved to, and the extension records themselves.
///
/// <para>Apart from <see cref="MftFixture"/> because each of these is several records and sometimes
/// clusters outside the table, kept consistent with one another, and most come in a version where
/// they disagree in one named way. The fixture assembles a table; this decides what a file spread
/// across it looks like.</para>
/// </summary>
public static class MftListedFiles
{
    private const uint StandardInformation = 0x10;
    private const uint FileName = 0x30;
    private const uint Data = 0x80;
    private const uint IndexAllocation = 0xA0;
    private const uint ReparsePoint = 0xC0;

    /// <summary>
    /// A long-named file whose list says its size is in <paramref name="extension"/>, while that
    /// record — its own, current, and well formed — holds instead a better-ranked name in the volume
    /// root and a symbolic link's reparse point. The record is refused for lacking the size, and
    /// nothing else it holds may count either: believed, it would move the file to the root and
    /// make it a link that occupies nothing.
    /// </summary>
    public static MftFixture AddLongNamedFileWhoseSizeRecordHoldsSomethingElse(
        this MftFixture fixture, uint number, uint parent, string name, string alias, uint extension)
    {
        var self = MftFixture.Reference(number);

        return fixture.Add(number, MftRecordBytes.Compose(
                isDirectory: false,
                baseReference: 0,
                MftRecordBytes.Sequence,
                fixture.BytesPerRecord,
                t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
                t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, 0, 0, nameSpace: 1),
                t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), alias, 0, 0, nameSpace: 2),
                t => MftAttributeBytes.WriteAttributeList(t,
                [
                    new ListedAttribute(StandardInformation, self),
                    new ListedAttribute(FileName, self),
                    new ListedAttribute(FileName, self),
                    new ListedAttribute(Data, MftFixture.Reference(extension)),
                ])))
            .Add(extension, MftRecordBytes.Compose(
                isDirectory: false,
                self,
                MftRecordBytes.Sequence,
                fixture.BytesPerRecord,
                t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(MftRecord.RootRecordNumber), "elsewhere.tgz", 0, 0),
                t => MftAttributeBytes.WriteReparsePoint(t, MftRecordBytes.SymbolicLinkTag)));
    }

    /// <summary>
    /// A junction whose own record holds its reparse point, and whose <c>$ATTRIBUTE_LIST</c>
    /// cannot be read. A file has one reparse point, so the list could not have put another
    /// anywhere: the junction is a link however unreadable the list is.
    /// </summary>
    public static MftFixture AddDirectoryLinkWithAMalformedAttributeList(
        this MftFixture fixture, uint number, uint parent, string name)
    {
        var value = MftAttributeBytes.AttributeListValue([new ListedAttribute(ReparsePoint, MftFixture.Reference(number))]);
        value[0x04] = 0xFF;

        return fixture.Add(number, MftRecordBytes.Compose(
            isDirectory: true,
            baseReference: 0,
            MftRecordBytes.Sequence,
            fixture.BytesPerRecord,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, 0, 0),
            t => MftAttributeBytes.WriteAttributeListValue(t, value),
            t => MftAttributeBytes.WriteReparsePoint(t, MftRecordBytes.MountPointTag)));
    }

    /// <summary>
    /// A file whose list names more extension records than the table can hold, each for one of its
    /// names: a table NTFS never wrote, and one a reader must not hold every want of.
    /// </summary>
    /// <param name="outsideTheRecord">
    /// Whether the list is kept in a cluster outside the table, where it can name far more records
    /// than one inside a record has room for.
    /// </param>
    public static MftFixture AddFileWhoseListWantsMoreRecordsThanTheTableHolds(
        this MftFixture fixture, uint number, bool outsideTheRecord)
    {
        // As many as fit: inside a record, the room a record has left after its header and dates;
        // outside it, one cluster.
        var count = outsideTheRecord ? 120 : 24;
        var self = MftFixture.Reference(number);
        IReadOnlyList<ListedAttribute> entries =
        [
            new ListedAttribute(StandardInformation, self),
            .. Enumerable.Range(0, count).Select(i => new ListedAttribute(FileName, MftFixture.Reference(1000 + (uint)i))),
        ];

        AttributeWriter list = t => MftAttributeBytes.WriteAttributeList(t, entries);

        if (outsideTheRecord)
        {
            const long ListCluster = 600;

            var value = MftAttributeBytes.AttributeListValue(entries);
            var cluster = new byte[MftRecordBytes.BytesPerCluster];
            value.CopyTo(cluster, 0);
            fixture.PutCluster(ListCluster, cluster);
            list = t => MftAttributeBytes.WriteNonResidentAttributeList(t, ListCluster, clusterCount: 1, value.Length);
        }

        return fixture.Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            fixture.BytesPerRecord,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            list));
    }

    /// <summary>
    /// A junction whose <c>$REPARSE_POINT</c> moved to <paramref name="extension"/>. Its target's
    /// entries belong to the target's own directory, so read as an ordinary directory it has no
    /// children and totals nothing, however much the path appears to hold.
    /// </summary>
    /// <param name="mismatch">
    /// Where given, the list and the extension record disagree in this way, and nothing then says
    /// whether the directory is a link.
    /// </param>
    public static MftFixture AddDirectoryLinkWithItsReparsePointInAnExtensionRecord(
        this MftFixture fixture, uint number, uint parent, string name, uint extension, ListMismatch? mismatch = null)
    {
        var (self, listed) = PlaceExtension(
            fixture, number, extension, mismatch, t => MftAttributeBytes.WriteReparsePoint(t, MftRecordBytes.MountPointTag));

        return fixture.Add(number, MftRecordBytes.Compose(
            isDirectory: true,
            baseReference: 0,
            MftRecordBytes.Sequence,
            fixture.BytesPerRecord,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, 0, 0),
            t => MftAttributeBytes.WriteAttributeList(t,
            [
                new ListedAttribute(StandardInformation, self),
                new ListedAttribute(FileName, self),
                .. listed.Select(l => new ListedAttribute(ReparsePoint, l)),
            ])));
    }

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
    /// <param name="alias">
    /// Where given, the base record keeps <paramref name="name"/> as its Win32 name and this as its
    /// separate 8.3 alias, as it does for any name too long for 8.3. Neither is in the best
    /// namespace, so nothing about the base record's names alone settles the file's place.
    /// </param>
    public static MftFixture AddFileWithDataInAnExtensionRecord(
        this MftFixture fixture,
        uint number,
        uint parent,
        string name,
        long allocated,
        long logical,
        uint extension,
        ListMismatch? mismatch = null,
        string? alias = null)
    {
        var (self, listed) = PlaceExtension(
            fixture,
            number, extension, mismatch, t => MftAttributeBytes.WriteNonResidentData(t, allocated, logical, startVirtualCluster: 0));

        if (alias is null)
        {
            return fixture.Add(number, ListingFile(fixture.BytesPerRecord, self, parent, name, [.. listed.Select(l => new ListedAttribute(Data, l))]));
        }

        return fixture.Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            fixture.BytesPerRecord,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, 0, 0, nameSpace: 1),
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), alias, 0, 0, nameSpace: 2),
            t => MftAttributeBytes.WriteAttributeList(t,
            [
                new ListedAttribute(StandardInformation, self),
                new ListedAttribute(FileName, self),
                new ListedAttribute(FileName, self),
                .. listed.Select(l => new ListedAttribute(Data, l)),
            ])));
    }

    /// <summary>
    /// The same, with the list itself grown too large for the base record. NTFS then keeps it in
    /// clusters outside the table, at <paramref name="listCluster"/> here, so following it takes a
    /// read the table's records cannot serve.
    /// </summary>
    public static MftFixture AddFileWithANonResidentAttributeList(
        this MftFixture fixture,
        uint number, uint parent, string name, long allocated, long logical, uint extension, long listCluster) =>
        AddFileWithAListInClusters(fixture, number, parent, name, allocated, logical, extension, listCluster, listCluster);

    /// <summary>
    /// The same, with the list's one run sparse: it has a length and no place on the disk, so it
    /// holds no entries at all. A complete list is placed at cluster 0 as well, so a reader that
    /// took the sparse run's start of zero as a real cluster would find one and follow it.
    /// </summary>
    public static MftFixture AddFileWithASparseAttributeList(
        this MftFixture fixture,
        uint number, uint parent, string name, long allocated, long logical, uint extension) =>
        AddFileWithAListInClusters(fixture, number, parent, name, allocated, logical, extension, listCluster: null, placedAt: 0);

    /// <summary>
    /// A file fragmented across two extension records. Only the piece starting at cluster 0 states
    /// the sizes, and here it sits in the later record: the earlier one holds a continuation whose
    /// size fields are zero, as NTFS leaves them.
    /// </summary>
    public static MftFixture AddFileWithDataSplitAcrossExtensionRecords(
        this MftFixture fixture,
        uint number, uint parent, string name, long allocated, long logical, uint continuation, uint start) =>
        fixture.Add(number, ListingFile(fixture.BytesPerRecord, MftFixture.Reference(number), parent, name,
            [
                new ListedAttribute(Data, MftFixture.Reference(start), LowestVcn: 0),
                new ListedAttribute(Data, MftFixture.Reference(continuation), LowestVcn: 4),
            ]))
            .Add(continuation, DataPiece(fixture.BytesPerRecord, number, allocated: 0, logical: 0, startVirtualCluster: 4))
            .Add(start, DataPiece(fixture.BytesPerRecord, number, allocated, logical, startVirtualCluster: 0));

    /// <summary>
    /// A file whose name moved into <paramref name="extension"/>, which is what NTFS does once a
    /// file has enough hard links to overflow its own record. A system volume is full of these, and
    /// the base record alone cannot say which directory the file is in.
    /// </summary>
    /// <param name="mismatch">
    /// Where given, the list and the extension record disagree in this way. Nothing then says which
    /// directory the file is in, so it could belong to any.
    /// </param>
    public static MftFixture AddFileWithItsNameInAnExtensionRecord(
        this MftFixture fixture,
        uint number, uint parent, string name, long allocated, long logical, uint extension, ListMismatch? mismatch = null)
    {
        var (self, listed) = PlaceExtension(
            fixture,
            number, extension, mismatch, t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, allocated, logical));

        return fixture.Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            fixture.BytesPerRecord,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteAttributeList(t,
            [
                new ListedAttribute(StandardInformation, self),
                .. listed.Select(l => new ListedAttribute(FileName, l)),
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
    public static MftFixture AddFileWithItsBetterNameInAnExtensionRecord(
        this MftFixture fixture,
        uint number,
        uint aliasParent,
        string alias,
        uint parent,
        string name,
        long logical,
        uint extension,
        ListMismatch? mismatch = null) =>
        AddFileWithANameInAnExtensionRecord(
            fixture,
            number,
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(aliasParent), alias, logical, logical, nameSpace: 2),
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, logical, logical, nameSpace: 1),
            logical,
            extension,
            mismatch);

    /// <summary>
    /// A file with two hard links: its own record keeps the one under <paramref name="parent"/>,
    /// and the one under <paramref name="otherParent"/> moved to <paramref name="extension"/>. Both
    /// are in the best namespace, so nothing in the extension record can outrank the name the base
    /// record already holds.
    /// </summary>
    public static MftFixture AddHardLinkedFileWithItsOtherNameInAnExtensionRecord(
        this MftFixture fixture,
        uint number,
        uint parent,
        string name,
        uint otherParent,
        string otherName,
        long logical,
        uint extension,
        ListMismatch? mismatch = null) =>
        AddFileWithANameInAnExtensionRecord(
            fixture,
            number,
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, logical, logical),
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(otherParent), otherName, logical, logical),
            logical,
            extension,
            mismatch);

    /// <summary>
    /// A file whose <c>$ATTRIBUTE_LIST</c> cannot be read: its one entry declares a length that
    /// runs past the end of the list. A list read in part may have lost the very entry saying
    /// where the size went.
    /// </summary>
    public static MftFixture AddFileWithAMalformedAttributeList(
        this MftFixture fixture, uint number, uint parent, string name)
    {
        var value = MftAttributeBytes.AttributeListValue([new ListedAttribute(Data, MftFixture.Reference(number))]);
        value[0x04] = 0xFF;

        return fixture.Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            fixture.BytesPerRecord,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, 0, 0),
            t => MftAttributeBytes.WriteAttributeListValue(t, value)));
    }

    /// <summary>
    /// A file fragmented across extents but still fully described here: an attribute list naming
    /// this record for every piece, then the extent starting at VCN 0 that carries the sizes, then
    /// a continuation extent that does not. The sizes are known, so this must not be confused with
    /// a record that has lost them.
    /// </summary>
    public static MftFixture AddFileSplitAcrossExtents(
        this MftFixture fixture, uint number, uint parent, string name, long allocated, long logical) =>
        fixture.Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            fixture.BytesPerRecord,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, allocated, logical),
            t => MftAttributeBytes.WriteAttributeList(t,
            [
                new ListedAttribute(StandardInformation, MftFixture.Reference(number)),
                new ListedAttribute(FileName, MftFixture.Reference(number)),
                new ListedAttribute(Data, MftFixture.Reference(number), LowestVcn: 0),
                new ListedAttribute(Data, MftFixture.Reference(number), LowestVcn: 4),
            ]),
            t => MftAttributeBytes.WriteNonResidentData(t, allocated, logical, startVirtualCluster: 0),
            t => MftAttributeBytes.WriteNonResidentData(t, allocated: 0, logical: 0, startVirtualCluster: 4)));

    /// <summary>
    /// A directory big enough that NTFS moved its index into <paramref name="extension"/>. Common on
    /// any real volume, and carrying no size of its own that anything counts, so nothing in the
    /// extension record is needed to measure it.
    /// </summary>
    public static MftFixture AddDirectoryWithAttributesInAnExtensionRecord(
        this MftFixture fixture, uint number, uint parent, string name, uint extension) =>
        fixture.Add(number, MftRecordBytes.Compose(
                isDirectory: true,
                baseReference: 0,
                MftRecordBytes.Sequence,
                fixture.BytesPerRecord,
                t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
                t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, 0, 0),
                t => MftAttributeBytes.WriteAttributeList(t,
                [
                    new ListedAttribute(StandardInformation, MftFixture.Reference(number)),
                    new ListedAttribute(FileName, MftFixture.Reference(number)),
                    new ListedAttribute(IndexAllocation, MftFixture.Reference(extension), Name: "$I30"),
                ])))
            .Add(extension, MftRecordBytes.Compose(isDirectory: true, MftFixture.Reference(number), MftRecordBytes.Sequence, fixture.BytesPerRecord));

    /// <summary>
    /// A file CompactOS compressed whose compressed stream NTFS moved to <paramref name="extension"/>,
    /// as it does once the stream's runs outgrow the base record. The base record keeps the file's
    /// own stream, sparse and holding nothing, so the file occupies only what the extension states.
    /// </summary>
    /// <param name="mismatch">
    /// Where given, the list and the extension record disagree in this way, as they do for a file
    /// caught mid-change.
    /// </param>
    public static MftFixture AddOverlayCompressedFileWithItsStreamInAnExtensionRecord(
        this MftFixture fixture,
        uint number,
        uint parent,
        string name,
        long logical,
        long compressed,
        uint extension,
        ListMismatch? mismatch = null)
    {
        var (self, listed) = PlaceExtension(
            fixture,
            number,
            extension,
            mismatch,
            t => MftAttributeBytes.WriteStream(t, MftRecordParser.WofStreamName, compressed, compressed));

        return fixture.Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            fixture.BytesPerRecord,
            t => MftAttributeBytes.WriteStandardInformation(
                t, 0, 0, FileAttributes.Archive | FileAttributes.SparseFile | FileAttributes.ReparsePoint),
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, 0, logical),
            t => MftAttributeBytes.WriteAttributeList(t,
            [
                new ListedAttribute(StandardInformation, self),
                new ListedAttribute(FileName, self),
                new ListedAttribute(Data, self),
                .. listed.Select(l => new ListedAttribute(Data, l, Name: MftRecordParser.WofStreamName)),
                new ListedAttribute(ReparsePoint, self),
            ]),
            t => MftAttributeBytes.WriteReparsePoint(t, MftRecordBytes.WindowsOverlayFilterTag),
            t => MftAttributeBytes.WriteStream(t, name: null, logical, logical, MftAttributeBytes.SparseFlag, occupied: 0)));
    }

    /// <summary>
    /// An extension record met on its own in the first pass. A real volume holds many, and none of
    /// them is a fault: the base record that owns it carries the file's identity.
    /// </summary>
    public static MftFixture AddExtensionRecord(
        this MftFixture fixture, uint number, uint baseRecordNumber) =>
        fixture.Add(number, MftRecordBytes.Compose(isDirectory: false, MftFixture.Reference(baseRecordNumber), MftRecordBytes.Sequence, fixture.BytesPerRecord));

    /// <param name="listCluster">Where the base record says the list is, or null for a sparse run.</param>
    /// <param name="placedAt">Where the list's bytes actually are.</param>
    private static MftFixture AddFileWithAListInClusters(
        MftFixture fixture,
        uint number, uint parent, string name, long allocated, long logical, uint extension, long? listCluster, long placedAt)
    {
        var value = MftAttributeBytes.AttributeListValue(ListOf(MftFixture.Reference(number), [new ListedAttribute(Data, MftFixture.Reference(extension))]));
        var cluster = new byte[MftRecordBytes.BytesPerCluster];
        value.CopyTo(cluster, 0);
        fixture.PutCluster(placedAt, cluster);

        return fixture.Add(number, MftRecordBytes.Compose(
                isDirectory: false,
                baseReference: 0,
                MftRecordBytes.Sequence,
                fixture.BytesPerRecord,
                t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
                t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, 0, 0),
                t => MftAttributeBytes.WriteNonResidentAttributeList(t, listCluster, clusterCount: 1, value.Length)))
            .Add(extension, DataPiece(fixture.BytesPerRecord, number, allocated, logical, startVirtualCluster: 0));
    }

    /// <summary>
    /// A file whose base record keeps one name, written by <paramref name="ownName"/>, and whose
    /// other name, written by <paramref name="otherName"/>, is in <paramref name="extension"/>.
    /// </summary>
    private static MftFixture AddFileWithANameInAnExtensionRecord(
        MftFixture fixture,
        uint number,
        AttributeWriter ownName,
        AttributeWriter otherName,
        long logical,
        uint extension,
        ListMismatch? mismatch)
    {
        var (self, listed) = PlaceExtension(fixture, number, extension, mismatch, otherName);

        return fixture.Add(number, MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            fixture.BytesPerRecord,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            ownName,
            t => MftAttributeBytes.WriteAttributeList(t,
            [
                new ListedAttribute(StandardInformation, self),
                new ListedAttribute(FileName, self),
                .. listed.Select(l => new ListedAttribute(FileName, l)),
                new ListedAttribute(Data, self),
            ]),
            t => MftAttributeBytes.WriteNonResidentData(t, logical, logical, startVirtualCluster: 0)));
    }

    /// <summary>
    /// Put <paramref name="content"/> in extension record <paramref name="extension"/>, owned by
    /// record <paramref name="number"/> and wrong in the way <paramref name="mismatch"/> names, if
    /// one is given. Returns how the owner's list names its own record, and every reference it holds
    /// to the extension record: one, unless the mismatch is a second, stale one.
    /// </summary>
    private static (ulong Self, IReadOnlyList<ulong> Listed) PlaceExtension(
        MftFixture fixture,
        uint number, uint extension, ListMismatch? mismatch, AttributeWriter content)
    {
        // A record number no fixture table reaches, so a read of it finds the end of the table.
        const uint PastTheTable = 0x00FF_FFFF;

        var stale = (ushort)(MftRecordBytes.Sequence + 1);
        var self = MftFixture.Reference(number);
        var owner = MftFixture.Reference(number);
        var sequence = MftRecordBytes.Sequence;
        AttributeWriter[] attributes = [content];

        switch (mismatch)
        {
            case ListMismatch.ItsOwnSequence:
                sequence = stale;
                break;

            case ListMismatch.OwnerNumber:
                owner = MftFixture.Reference(number + 1);
                break;

            case ListMismatch.OwnerSequence:
                owner = number | ((ulong)stale << 48);
                break;

            case ListMismatch.HoldsNothingListed:
                attributes = [];
                break;

            case ListMismatch.OutsideTheTable:
                return (self, [MftFixture.Reference(PastTheTable)]);

            // Before the true one, so a reader that kept one reference per record and let the last
            // decide would keep the true one, accept the record, and never notice the stale entry.
            case ListMismatch.ListedTwiceBySequence:
                fixture.Add(extension, MftRecordBytes.Compose(isDirectory: false, owner, sequence, fixture.BytesPerRecord, attributes));
                return (self, [extension | ((ulong)(MftRecordBytes.Sequence - 1) << 48), MftFixture.Reference(extension)]);

            case ListMismatch.ListNamesItsOwnRecordAsItWas:
                self = number | ((ulong)stale << 48);
                break;
        }

        fixture.Add(extension, MftRecordBytes.Compose(isDirectory: false, owner, sequence, fixture.BytesPerRecord, attributes));
        return (self, [MftFixture.Reference(extension)]);
    }

    /// <summary>
    /// A base record keeping its name and its dates, with a list naming the record as
    /// <paramref name="self"/> for those and <paramref name="elsewhere"/> for the rest.
    /// </summary>
    private static byte[] ListingFile(
        int bytesPerRecord, ulong self, uint parent, string name, IReadOnlyList<ListedAttribute> elsewhere) =>
        MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            bytesPerRecord,
            t => MftAttributeBytes.WriteStandardInformation(t, 0, 0),
            t => MftAttributeBytes.WriteFileName(t, MftFixture.Reference(parent), name, 0, 0),
            t => MftAttributeBytes.WriteAttributeList(t, ListOf(self, elsewhere)));

    private static IReadOnlyList<ListedAttribute> ListOf(ulong self, IReadOnlyList<ListedAttribute> elsewhere) =>
    [
        new ListedAttribute(StandardInformation, self),
        new ListedAttribute(FileName, self),
        .. elsewhere,
    ];

    /// <summary>An extension record holding one piece of <paramref name="owner"/>'s unnamed <c>$DATA</c>.</summary>
    private static byte[] DataPiece(int bytesPerRecord, uint owner, long allocated, long logical, long startVirtualCluster) =>
        MftRecordBytes.Compose(
            isDirectory: false,
            MftFixture.Reference(owner),
            MftRecordBytes.Sequence,
            bytesPerRecord,
            t => MftAttributeBytes.WriteNonResidentData(t, allocated, logical, startVirtualCluster));
}
