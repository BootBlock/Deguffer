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

    private readonly List<byte[]> _records = [];

    private readonly int _bytesPerRecord;

    private readonly Dictionary<long, byte[]> _clusters = [];

    /// <summary>
    /// Every gap in the table, shared. A source copies what it serves, and <see cref="Corrupt"/>
    /// damages a copy, so a test can place a record past a hundred thousand others for the cost of
    /// the references.
    /// </summary>
    private readonly byte[] _blank;

    private long _unreadableFrom = long.MaxValue;

    /// <param name="bytesPerRecord">
    /// 1,024 on a disk with 512-byte sectors, and 4,096 on a disk with native 4,096-byte sectors,
    /// where NTFS sizes a record to one sector.
    /// </param>
    public MftFixture(int bytesPerRecord = MftRecordBytes.BytesPerRecord)
    {
        _bytesPerRecord = bytesPerRecord;
        _blank = new byte[bytesPerRecord];

        // Records 0-4 are NTFS's own named metadata files ($MFT, $MFTMirr, $LogFile, $Volume,
        // $AttrDef), left blank here: an unused entry is skipped by the parser, which is worth
        // exercising rather than working around. Record 5 is the root.
        for (var i = 0; i < MftRecord.RootRecordNumber; i++)
        {
            _records.Add(new byte[_bytesPerRecord]);
        }

        // The root is its own parent — the shape the index has to detect to avoid a cyclic walk.
        _records.Add(MftRecordBytes.Build(
            MftRecord.RootRecordNumber,
            ".",
            isDirectory: true,
            MftRecordBytes.DirectoryStreamBytes,
            MftRecordBytes.DirectoryStreamBytes,
            DataPlacement.NonResident,
            bytesPerRecord: _bytesPerRecord));

        // 6 to 11 are the rest of the named metadata, blank for the same reason as 0 to 4.
        while (_records.Count < 12)
        {
            _records.Add(new byte[_bytesPerRecord]);
        }

        // 12 to 15 are not blank on a real volume, and this is the whole point of filling them in.
        // NTFS holds them back for future metadata, marks them in use, and gives them neither a
        // $FILE_NAME nor an $ATTRIBUTE_LIST. Leaving them out of the fixture is what let a builder
        // that abandons the volume on that shape stay green through every test here while §5.5's
        // fast path could not engage on any real machine. A fake that models an idealised volume
        // proves the reader works on a volume nobody has.
        while (_records.Count < MftRecord.ReservedRecordCount)
        {
            _records.Add(MftRecordBytes.RecordWithoutAName(_bytesPerRecord));
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
            FileTime(lastWritten),
            bytesPerRecord: _bytesPerRecord));

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
            MftRecordBytes.MountPointTag,
            bytesPerRecord: _bytesPerRecord));

    /// <summary>
    /// A file that is a link rather than the thing it names — a symbolic link, or a placeholder a
    /// storage tier left behind. It declares a size and occupies none of it here, so a reader that
    /// counts it disagrees with a walk, which does not enter reparse points at all.
    /// </summary>
    public MftFixture AddFileLink(uint number, uint parent, string name, long logical) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical, DataPlacement.NonResident,
            MftRecordBytes.SymbolicLinkTag,
            bytesPerRecord: _bytesPerRecord));

    /// <summary>
    /// A file compressed in place by the Windows Overlay Filter. It carries a reparse point and is
    /// not a link: the content is there, and a walk counts it because the filter hides the reparse
    /// attribute from an ordinary enumeration.
    /// </summary>
    public MftFixture AddOverlayCompressedFile(uint number, uint parent, string name, long allocated, long logical) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated, logical, DataPlacement.NonResident,
            MftRecordBytes.WindowsOverlayFilterTag,
            bytesPerRecord: _bytesPerRecord));

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
            reparseTag: 0, FileTime(created), FileTime(lastWritten),
            bytesPerRecord: _bytesPerRecord));

    /// <summary>
    /// A file whose record carries no <c>$STANDARD_INFORMATION</c>, so nothing can date it. It still
    /// has a name, a parent and a size, and it still has to place and draw —
    /// <see cref="MftRecordBytes.FileWithoutTimestamps"/> says why refusing it would be the wrong
    /// trade.
    /// </summary>
    public MftFixture AddFileWithNoTimestamps(uint number, uint parent, string name, long logical) =>
        Add(number, MftRecordBytes.FileWithoutTimestamps(Reference(parent), name, logical, _bytesPerRecord));

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
            reparseTag: 0, FileTime(created), FileTime(lastWritten),
            bytesPerRecord: _bytesPerRecord));

    /// <summary>
    /// A file with no unnamed <c>$DATA</c> at all, as a symbolic link has: its content is somewhere
    /// else entirely. Occupying nothing is the true answer here, and it has to stay distinguishable
    /// from a size the reader failed to establish.
    /// </summary>
    public MftFixture AddFileWithNoDataStream(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.NoData,
            bytesPerRecord: _bytesPerRecord));

    /// <summary>
    /// A file whose base record holds a later extent of a split <c>$DATA</c> rather than the first.
    /// Only the extent starting at VCN 0 carries the sizes; the rest leave those fields zero, so a
    /// reader that trusts them reads a real file as empty.
    /// </summary>
    public MftFixture AddFileDescribingOnlyALaterExtent(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.LaterExtent,
            bytesPerRecord: _bytesPerRecord));

    /// <summary>
    /// A non-resident <c>$DATA</c> whose declared length stops before the size fields — a corrupt
    /// record, and one whose sizes cannot be read rather than being zero.
    /// </summary>
    public MftFixture AddFileWithATruncatedDataHeader(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.TruncatedHeader,
            bytesPerRecord: _bytesPerRecord));

    /// <summary>The same corruption in a resident <c>$DATA</c>, where the length field itself is cut off.</summary>
    public MftFixture AddFileWithATruncatedResidentDataHeader(uint number, uint parent, string name) =>
        Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated: 0, logical: 0, DataPlacement.TruncatedResidentHeader,
            bytesPerRecord: _bytesPerRecord));

    /// <summary>
    /// A record in use, holding data, claiming no identity and with no attribute list pointing
    /// anywhere else for one. No healthy volume produces this.
    /// </summary>
    public MftFixture AddRecordWithNoIdentityAtAll(uint number) =>
        Add(number, MftRecordBytes.RecordWithoutAName(_bytesPerRecord));

    /// <summary>
    /// A record naming a parent beyond the 32-bit range the index addresses. Narrowing this
    /// silently would wrap it onto an unrelated record and graft a subtree somewhere it never was.
    /// </summary>
    public MftFixture AddFileWithUnaddressableParent(uint number, string name, long allocated) =>
        Add(number, MftRecordBytes.Build(
            0x1_0000_0007UL | (1UL << 48), name, isDirectory: false, allocated, allocated, DataPlacement.NonResident,
            bytesPerRecord: _bytesPerRecord));

    /// <summary>
    /// A file whose name is sized so that its <c>$DATA</c> allocated-size field lies across the
    /// first fixup stride boundary, and so is one of the fields NTFS displaces into the update
    /// sequence array.
    ///
    /// Without a record shaped like this the fixup is untested: short records leave the boundary
    /// sitting in trailing zeroes, where failing to restore the displaced bytes changes nothing.
    /// On a real volume the boundary lands in live attribute data, and two unrestored bytes inside
    /// a 64-bit size field alter it by up to 2^48 — a wrong number, reported confidently.
    /// </summary>
    public MftFixture AddFileWithSizeAcrossStrideBoundary(uint number, uint parent, long allocated, long logical)
    {
        var name = new string('n', MftRecordBytes.NameLengthPuttingSizeFieldAcrossBoundary(_bytesPerRecord));

        return Add(number, MftRecordBytes.Build(
            Reference(parent), name, isDirectory: false, allocated, logical, DataPlacement.NonResident,
            bytesPerRecord: _bytesPerRecord));
    }

    /// <summary>Blank out a record, standing in for a free or never-used entry.</summary>
    public MftFixture AddUnused(uint number) => Add(number, new byte[_bytesPerRecord]);

    /// <summary>
    /// Break one stride's update sequence stamp, as a torn write would. The record must then be
    /// rejected outright — a half-fixed-up record parses cleanly and reports a wrong size.
    /// </summary>
    public MftFixture CorruptSectorStamp(uint number) =>
        Corrupt(number, record => record[MftRecordBytes.FixupStride - 1] ^= 0xFF);

    /// <summary>
    /// Make a record's first attribute declare itself far longer than the record, as a corrupt
    /// sector the stamps do not cover would. The record must be rejected, not thrown on.
    /// </summary>
    public MftFixture CorruptAttributeLength(uint number) =>
        Corrupt(number, record => MftRecordBytes.DeclareFirstAttributeLength(record, MftRecordBytes.LengthJustUnderIntMax));

    /// <summary>
    /// Make a record's <c>$FILE_NAME</c> declare a value far longer than the attribute around it.
    /// The record then has no name it can be placed by, and must be rejected, not thrown on.
    /// </summary>
    public MftFixture CorruptFileNameValueLength(uint number) =>
        Corrupt(number, record => MftRecordBytes.DeclareFileNameValueLength(record, MftRecordBytes.LengthJustUnderIntMax));

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
            _bytesPerRecord,
            _unreadableFrom,
            MftRecordBytes.BytesPerCluster,
            _clusters);

    /// <summary>
    /// A parent as NTFS stores it: record number in the low 48 bits, reuse sequence above. The
    /// sequence is deliberately non-zero, because a reader that forgets to mask it off still works
    /// on a freshly formatted volume and fails on a used one.
    /// </summary>
    internal static ulong Reference(uint recordNumber) => recordNumber | (1UL << 48);

    /// <summary>
    /// A <see cref="DateTime"/> as NTFS stores one, or zero for "never set".
    ///
    /// <para>Converted through <see cref="DateTime.ToFileTimeUtc"/> rather than by arithmetic here,
    /// so a fixture and the reader under test are not two copies of the same epoch calculation
    /// agreeing with each other about a mistake.</para>
    /// </summary>
    private static long FileTime(DateTime? when) => when?.ToFileTimeUtc() ?? 0;

    /// <summary>
    /// Damage a copy of a record and put the copy in its place. A gap in the table is the one
    /// shared blank record, so damaging a record where it lies would damage every gap with it.
    /// </summary>
    /// <summary>How long each record in this fixture's table is.</summary>
    internal int BytesPerRecord => _bytesPerRecord;

    /// <summary>Place the bytes of one cluster outside the table, for a source to serve.</summary>
    internal void PutCluster(long cluster, byte[] bytes) => _clusters[cluster] = bytes;

    private MftFixture Corrupt(uint number, Action<byte[]> corrupt)
    {
        var record = (byte[])_records[(int)number].Clone();
        corrupt(record);
        _records[(int)number] = record;
        return this;
    }

    internal MftFixture Add(uint number, byte[] record)
    {
        while (_records.Count <= number)
        {
            _records.Add(_blank);
        }

        _records[(int)number] = record;
        return this;
    }
}
