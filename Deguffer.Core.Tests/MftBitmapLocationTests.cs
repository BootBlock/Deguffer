using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Finding <c>$MFT</c>'s <c>$BITMAP</c> in the records the table's own extents are read from, and
/// reading it. Whatever does not add up costs the bitmap and never the table: a pass without one
/// reads every record.
///
/// <para>The fragmented tables here are <see cref="MftExtentMapReaderTests"/>'s: 32 records in four
/// extents, with <c>$DATA</c> in records 0, 16 and 19.</para>
/// </summary>
public sealed class MftBitmapLocationTests
{
    private const long DataSize = 32 * FragmentedMftVolume.BytesPerRecord;

    /// <summary>Two clusters of 512 bytes: longer than one, so it can be split across two pieces.</summary>
    private const long BitmapLength = 600;

    private static readonly DataRun[] Layout =
        [new DataRun(1_000, 33), new DataRun(2_000, 3), new DataRun(3_000, 8), new DataRun(4_000, 20)];

    private static readonly MftPiece[] Pieces =
    [
        new MftPiece(Holder: 0, LowestVcn: 0, [Layout[0], Layout[1]]),
        new MftPiece(Holder: 16, LowestVcn: 36, [Layout[2]]),
        new MftPiece(Holder: 19, LowestVcn: 44, [Layout[3]]),
    ];

    [Fact]
    public void LocatesTheBitmapRecordZeroHolds()
    {
        var record = MftRecordBytes.SelfRecord(
            [new DataRun(786_432, 64)], dataSize: 65_536, bitmap: ([new DataRun(800_000, 1)], 16));

        Assert.True(MftExtentMapReader.TryRead(record, bytesPerCluster: 4096, MftExtentMapTests.NoClusters, out _, out var bitmap));

        Assert.NotNull(bitmap);
        Assert.Equal([new DataRun(800_000, 1)], bitmap.Runs);
        Assert.Equal(16, bitmap.Length);
    }

    /// <summary>A tiny table keeps its bitmap inside record 0, and its bytes are taken from there.</summary>
    [Fact]
    public void TakesABitmapKeptInsideRecordZero()
    {
        byte[] bits = [0xFF, 0x0F, 0, 0, 0, 0, 0, 0];

        var record = SelfRecordWith(t => MftAttributeBytes.WriteResidentMftBitmap(t, bits));

        Assert.True(MftExtentMapReader.TryRead(record, bytesPerCluster: 4096, MftExtentMapTests.NoClusters, out _, out var bitmap));

        Assert.Equal(bits, bitmap!.Value);
        Assert.Empty(bitmap.Runs);
    }

    /// <summary>Only what NTFS has written is worth reading. The rest of the clusters can hold anything.</summary>
    [Fact]
    public void ReadsNoFurtherThanWhatWasWritten()
    {
        var record = SelfRecordWith(t => MftAttributeBytes.WriteMftBitmap(t, [new DataRun(800_000, 1)], length: 600, initializedSize: 100));

        Assert.True(MftExtentMapReader.TryRead(record, bytesPerCluster: 4096, MftExtentMapTests.NoClusters, out _, out var bitmap));

        Assert.Equal(100, bitmap!.Length);
    }

    /// <summary>
    /// A bitmap in an extension record that holds none of the table's own extents is found through
    /// the finished map, from the list's entry for it.
    /// </summary>
    [Fact]
    public void LocatesABitmapInAnExtensionRecordOnlyTheListNames()
    {
        var volume = new FragmentedMftVolume(
            Layout, DataSize, Pieces, bitmap: ([new MftPiece(Holder: 20, LowestVcn: 0, [new DataRun(7_000, 2)])], BitmapLength));

        Assert.True(TryRead(volume, out var map, out var bitmap));

        Assert.Equal(Layout, map.Runs);
        Assert.Equal([new DataRun(7_000, 2)], bitmap!.Runs);
        Assert.Equal(BitmapLength, bitmap.Length);
    }

    [Fact]
    public void JoinsABitmapSplitAcrossRecords()
    {
        var volume = new FragmentedMftVolume(
            Layout,
            DataSize,
            Pieces,
            bitmap: (
                [
                    new MftPiece(Holder: 0, LowestVcn: 0, [new DataRun(7_000, 1)]),
                    new MftPiece(Holder: 16, LowestVcn: 1, [new DataRun(7_100, 1)]),
                ],
                BitmapLength));

        Assert.True(TryRead(volume, out _, out var bitmap));
        Assert.Equal([new DataRun(7_000, 1), new DataRun(7_100, 1)], bitmap!.Runs);
    }

    /// <summary>
    /// Pieces that leave a gap describe some of the bitmap, which is not the bitmap. The bitmap here
    /// fits in the first piece, so it is the gap alone that says the pieces are wrong.
    /// </summary>
    [Fact]
    public void DropsABitmapWhosePiecesDoNotMeetAndKeepsTheTable()
    {
        var volume = new FragmentedMftVolume(
            Layout,
            DataSize,
            Pieces,
            bitmap: (
                [
                    new MftPiece(Holder: 0, LowestVcn: 0, [new DataRun(7_000, 1)]),
                    new MftPiece(Holder: 16, LowestVcn: 2, [new DataRun(7_100, 1)]),
                ],
                400));

        Assert.True(TryRead(volume, out var map, out var bitmap));
        Assert.Equal(Layout, map.Runs);
        Assert.Null(bitmap);
    }

    /// <summary>
    /// Where record 0 has a list, the list names every piece. A piece it does not name, and a named
    /// piece in a record reused since, mean the list and the records disagree.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DropsABitmapTheListDisagreesWithAndKeepsTheTable(bool reused)
    {
        IReadOnlyList<ListedAttribute> list =
        [
            .. Pieces.Select(p => new ListedAttribute(0x80, FragmentedMftVolume.Reference(p.Holder, MftRecordBytes.Sequence), p.LowestVcn)),
            .. reused
                ? [new ListedAttribute(0xB0, FragmentedMftVolume.Reference(20, MftRecordBytes.Sequence + 1))]
                : Array.Empty<ListedAttribute>(),
        ];

        var volume = new FragmentedMftVolume(
            Layout,
            DataSize,
            Pieces,
            list,
            bitmap: ([new MftPiece(Holder: reused ? 20 : 0, LowestVcn: 0, [new DataRun(7_000, 2)])], BitmapLength));

        Assert.True(TryRead(volume, out var map, out var bitmap));
        Assert.Equal(Layout, map.Runs);
        Assert.Null(bitmap);
    }

    /// <summary>A pass reads only the bits of the records it reads, however long the bitmap says it is.</summary>
    [Fact]
    public void ReadsOnlyTheBitsOfTheRecordsAsked()
    {
        using var source = new MftFixture().AddUnused(999).Build();

        var bitmap = MftBitmapReader.TryRead(source, count: 20);

        Assert.Equal(24, bitmap!.Known);
    }

    /// <summary>
    /// A bitmap that cannot be read is no bitmap: the pass reads every record, free ones included,
    /// rather than any fewer.
    /// </summary>
    [Fact]
    public void ReadsEveryRecordWhereTheBitmapCannotBeRead()
    {
        using var table = new MftFixture()
            .AddFile(40, MftRecord.RootRecordNumber, "a.bin", allocated: 4096, logical: 4000)
            .WithoutBitmap()
            .Build();
        using var source = new BitmapOf(table, MftBitmapPlacement.InClusters([new DataRun(MftFixture.BitmapCluster, 1)], 8));

        Assert.Null(MftBitmapReader.TryRead(source, source.RecordCount));

        var handed = 0;
        MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            TableTuning.Default,
            (_, _, in _) => Interlocked.Increment(ref handed) > 0,
            onProgress: null,
            default);

        Assert.Equal(table.RecordCount, handed);
    }

    private static byte[] SelfRecordWith(AttributeWriter bitmap) =>
        MftRecordBytes.Compose(
            isDirectory: false,
            baseReference: 0,
            MftRecordBytes.Sequence,
            MftRecordBytes.BytesPerRecord,
            [
                t => MftAttributeBytes.WriteStandardInformation(t, created: 0, lastWritten: 0),
                t => MftAttributeBytes.WriteMftData(t, [new DataRun(786_432, 64)], 65_536),
                bitmap,
            ]);

    private static bool TryRead(FragmentedMftVolume volume, out MftExtentMap map, out MftBitmapPlacement? bitmap) =>
        MftExtentMapReader.TryRead(volume.Record0, FragmentedMftVolume.BytesPerCluster, volume.TryReadClusters, out map, out bitmap);

    /// <summary>A table whose bitmap is said to be at <paramref name="bitmap"/>, wherever it really is.</summary>
    private sealed class BitmapOf(IMftSource table, MftBitmapPlacement? bitmap) : IMftSource
    {
        public int BytesPerRecord => table.BytesPerRecord;

        public long RecordCount => table.RecordCount;

        public MftBitmapPlacement? Bitmap => bitmap;

        public int BatchLength(long firstRecord, int capacity) => table.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination) => table.ReadBatch(firstRecord, destination);

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            table.ReadBatchAsync(firstRecord, destination, ct);

        public int BytesPerCluster => table.BytesPerCluster;

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => table.TryReadClusters(firstCluster, destination);

        public void Dispose()
        {
        }
    }
}
