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

    private bool _withBitmap = true;

    /// <summary>Records whose bit says otherwise than their header, and what it says.</summary>
    private readonly Dictionary<long, bool> _bitOverrides = [];

    /// <summary>
    /// Where a fixture source keeps <c>$MFT</c>'s <c>$BITMAP</c>: far from anything a test places, so
    /// a reader that looks for it elsewhere finds nothing.
    /// </summary>
    internal const long BitmapCluster = 7_000_000;

    /// <param name="bytesPerRecord">
    /// 1,024 unless the volume was formatted with large records, which are 4,096. Not tied to the
    /// sector size: Windows gives a disk with 4,096-byte sectors 1,024-byte records by default.
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
    /// A file compressed in place by the Windows Overlay Filter, laid out as the filter leaves it:
    /// its own stream sparse and holding nothing, and its content in the named stream the filter
    /// moved it to, which occupies <paramref name="allocated"/>. It carries a reparse point and is
    /// not a link, and a walk counts it at its length because the filter hides both from a listing.
    /// </summary>
    public MftFixture AddOverlayCompressedFile(uint number, uint parent, string name, long allocated, long logical) =>
        Add(number, MftRecordBytes.Stored(
            Reference(parent), name, logical,
            FileAttributes.Archive | FileAttributes.SparseFile | FileAttributes.ReparsePoint,
            MftAttributeBytes.SparseFlag, occupied: 0, MftRecordBytes.WindowsOverlayFilterTag,
            wofOccupied: allocated, bytesPerRecord: _bytesPerRecord));

    /// <summary>
    /// A cloud file with only <paramref name="onDisk"/> of its <paramref name="logical"/> bytes on
    /// this PC, as a sync app's placeholder is stored: sparse, marked for recall and offline, and
    /// carrying a reparse point that is not a link. Zero is OneDrive's "online-only".
    /// </summary>
    public MftFixture AddCloudFile(uint number, uint parent, string name, long logical, long onDisk = 0) =>
        Add(number, MftRecordBytes.Stored(
            Reference(parent), name, logical,
            FileAttributes.Archive | FileAttributes.SparseFile | FileAttributes.ReparsePoint
                | FileAttributes.Offline | (FileAttributes)0x0040_0000,
            MftAttributeBytes.SparseFlag, onDisk, MftRecordBytes.CloudFilesTag,
            bytesPerRecord: _bytesPerRecord));

    /// <summary>
    /// A file carrying <paramref name="attributes"/>, and the reparse point <paramref name="reparseTag"/>
    /// names where it is not zero, whose stream occupies its length.
    /// </summary>
    public MftFixture AddFileWithAttributes(
        uint number, uint parent, string name, long logical, FileAttributes attributes, uint reparseTag = 0) =>
        Add(number, MftRecordBytes.Stored(
            Reference(parent), name, logical, attributes, flags: 0, occupied: logical, reparseTag,
            bytesPerRecord: _bytesPerRecord));

    /// <summary>A file NTFS compressed into <paramref name="onDisk"/> bytes of clusters.</summary>
    public MftFixture AddCompressedFile(uint number, uint parent, string name, long logical, long onDisk) =>
        Add(number, MftRecordBytes.Stored(
            Reference(parent), name, logical, FileAttributes.Archive | FileAttributes.Compressed,
            MftAttributeBytes.CompressedFlag, onDisk, bytesPerRecord: _bytesPerRecord));

    /// <summary>A sparse file whose written ranges hold <paramref name="onDisk"/> bytes of clusters.</summary>
    public MftFixture AddSparseFile(uint number, uint parent, string name, long logical, long onDisk) =>
        Add(number, MftRecordBytes.Stored(
            Reference(parent), name, logical, FileAttributes.Archive | FileAttributes.SparseFile,
            MftAttributeBytes.SparseFlag, onDisk, bytesPerRecord: _bytesPerRecord));

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

    /// <summary>
    /// Leave <c>$MFT</c>'s <c>$BITMAP</c> out, as a source that cannot locate it does, so a pass
    /// reads and parses every record.
    /// </summary>
    public MftFixture WithoutBitmap()
    {
        _withBitmap = false;
        return this;
    }

    /// <summary>
    /// Clear the bit of a record in use, as for a file created after the bitmap was read. A pass
    /// believes the bit, so the record is never read.
    /// </summary>
    public MftFixture MarkFree(uint number)
    {
        _bitOverrides[number] = false;
        return this;
    }

    /// <summary>
    /// Set the bit of a free record, as for a file deleted after the bitmap was read. The record's
    /// header still says it is free, and the header decides.
    /// </summary>
    public MftFixture MarkInUse(uint number)
    {
        _bitOverrides[number] = true;
        return this;
    }

    /// <summary>
    /// The table as a source. It carries <c>$MFT</c>'s <c>$BITMAP</c> unless
    /// <see cref="WithoutBitmap"/> was asked for, set for every record whose header says it is in use,
    /// as NTFS keeps it, and kept in clusters of its own at <see cref="BitmapCluster"/>.
    /// </summary>
    public IMftSource Build()
    {
        var clusters = new Dictionary<long, byte[]>(_clusters);
        MftBitmapPlacement? placement = null;

        if (_withBitmap)
        {
            var bits = BitmapBytes();
            placement = MftBitmapPlacement.InClusters(Place(clusters, BitmapCluster, bits), bits.Length);
        }

        return new FixtureMftSource(
            _records,
            _bytesPerRecord,
            _unreadableFrom,
            MftRecordBytes.BytesPerCluster,
            clusters,
            placement);
    }

    /// <summary>
    /// One bit a record, set where the record's header says it is in use, then as
    /// <see cref="MarkFree"/> and <see cref="MarkInUse"/> say. Rounded up to eight bytes, as NTFS
    /// grows it.
    /// </summary>
    internal byte[] BitmapBytes()
    {
        var bits = new byte[(_records.Count + 63) / 64 * 8];

        for (var i = 0; i < _records.Count; i++)
        {
            var inUse = _bitOverrides.TryGetValue(i, out var overridden)
                ? overridden
                : MftRecordBytes.IsInUse(_records[i]);

            if (inUse)
            {
                bits[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return bits;
    }

    /// <summary>Lay <paramref name="bytes"/> out in whole clusters from <paramref name="first"/>.</summary>
    private static IReadOnlyList<DataRun> Place(Dictionary<long, byte[]> clusters, long first, byte[] bytes)
    {
        const int ClusterBytes = MftRecordBytes.BytesPerCluster;
        var count = Math.Max(1, (bytes.Length + ClusterBytes - 1) / ClusterBytes);

        for (var i = 0; i < count; i++)
        {
            var cluster = new byte[ClusterBytes];
            var start = i * ClusterBytes;
            bytes.AsSpan(start, Math.Min(ClusterBytes, bytes.Length - start)).CopyTo(cluster);
            clusters[first + i] = cluster;
        }

        return [new DataRun(first, count)];
    }

    /// <summary>
    /// This table laid out on a whole volume image, boot sector and all, for a test to open the
    /// way a real volume is opened. Record 0 becomes <c>$MFT</c>'s own record, describing where the
    /// table was placed.
    /// </summary>
    /// <param name="gapAfterCluster">
    /// Where to split the table into two extents with a gap between them, counted in clusters
    /// from its start. Null keeps it in one.
    /// </param>
    internal SectorStrictVolume BuildVolume(int bytesPerSector, int bytesPerCluster, long? gapAfterCluster = null)
    {
        if (_clusters.Count != 0 || _unreadableFrom != long.MaxValue)
        {
            throw new InvalidOperationException("A volume image models a table and nothing outside it.");
        }

        return NtfsVolumeImage.Build(
            _records, _bytesPerRecord, bytesPerSector, bytesPerCluster, gapAfterCluster, _withBitmap ? BitmapBytes() : null);
    }

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
