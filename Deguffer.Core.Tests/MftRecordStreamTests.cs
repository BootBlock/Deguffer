using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the record stream says about the table as a whole, apart from what either caller makes of
/// the records it hands on.
/// </summary>
public sealed class MftRecordStreamTests
{
    /// <summary>
    /// Each extension record belongs to one base record, so a table whose lists want more of them
    /// than it holds was not written by NTFS. Following every want of a hostile one would hold
    /// memory without bound, so the stream stops and says the table was not read in full, which
    /// both callers already know how to fall back from. Inside a record the wants are counted in
    /// the first pass, and outside one in the second.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaysATableWantingMoreExtensionRecordsThanItHoldsWasNotReadInFull(bool outsideTheRecord)
    {
        using var source = new MftFixture()
            .AddFileWhoseListWantsMoreRecordsThanTheTableHolds(20, outsideTheRecord)
            .Build();

        Assert.True(source.RecordCount < 24, "the fixture's list must name more records than the table holds");
        Assert.False(MftRecordStream.TryReadAll(source, (int)source.RecordCount, TableTuning.Default, static (_, _, in _) => true, onProgress: null, default));
    }

    /// <summary>
    /// Where the first pass meets the record that takes the wants past the table's size, it stops
    /// there rather than reading on and holding the wants of every record after it. Read without a
    /// bitmap, so the free record just before it is handed on too.
    /// </summary>
    [Fact]
    public void StopsReadingAtTheRecordWhoseWantsOutgrowTheTable()
    {
        using var source = new MftFixture()
            .AddFileWhoseListWantsMoreRecordsThanTheTableHolds(20, outsideTheRecord: false)
            .AddFile(21, MftRecord.RootRecordNumber, "after.tgz", allocated: 4096, logical: 4000)
            .WithoutBitmap()
            .Build();
        var handed = new List<long>();

        MftRecordStream.TryReadAll(source, (int)source.RecordCount, TableTuning.Default, (number, _, in _) =>
        {
            handed.Add(number);
            return true;
        }, onProgress: null, default);

        Assert.Contains(19L, handed);
        Assert.DoesNotContain(21L, handed);
    }

    /// <summary>
    /// The second pass reads extension records close together in one read, and never reads across
    /// a long run of records nobody wants to reach the next, however large a read may be.
    /// </summary>
    [Fact]
    public void ReadsExtensionRecordsFarApartSeparately()
    {
        using var table = new MftFixture()
            .AddFileWithDataInAnExtensionRecord(20, MftRecord.RootRecordNumber, "a.tgz", allocated: 8192, logical: 8000, extension: 1_000)
            .AddFileWithDataInAnExtensionRecord(21, MftRecord.RootRecordNumber, "b.tgz", allocated: 8192, logical: 8000, extension: 1_002)
            .AddFileWithDataInAnExtensionRecord(22, MftRecord.RootRecordNumber, "c.tgz", allocated: 8192, logical: 8000, extension: 3_000)
            .Build();
        using var source = new RecordedReads(table);

        Assert.True(MftRecordStream.TryReadAll(
            source,
            (int)source.RecordCount,
            new TableTuning(TableTuning.MaximumReadBytes, 4, 2),
            static (_, _, in _) => true,
            onProgress: null,
            default));

        Assert.Equal([(1_000L, 3), (3_000L, 1)], source.Reads);
    }

    /// <summary>The same stream reads a table whose lists want only what it holds to its end.</summary>
    [Fact]
    public void ReadsATableWhoseListsWantOnlyWhatItHolds()
    {
        using var source = new MftFixture()
            .AddFileWithDataInAnExtensionRecord(20, MftRecord.RootRecordNumber, "fragmented.tgz", allocated: 8192, logical: 8000, extension: 21)
            .Build();

        Assert.True(MftRecordStream.TryReadAll(source, (int)source.RecordCount, TableTuning.Default, static (_, _, in _) => true, onProgress: null, default));
    }

    /// <summary>
    /// A table that records the reads made one at a time, which only the second pass makes: the first
    /// pass reads overlapped.
    /// </summary>
    private sealed class RecordedReads(IMftSource table) : IMftSource
    {
        public List<(long First, int Records)> Reads { get; } = [];

        public int BytesPerRecord => table.BytesPerRecord;

        public long RecordCount => table.RecordCount;

        public MftBitmapPlacement? Bitmap => table.Bitmap;

        public int BatchLength(long firstRecord, int capacity) => table.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination)
        {
            Reads.Add((firstRecord, destination.Length / BytesPerRecord));
            return table.ReadBatch(firstRecord, destination);
        }

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            table.ReadBatchAsync(firstRecord, destination, ct);

        public int BytesPerCluster => table.BytesPerCluster;

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => table.TryReadClusters(firstCluster, destination);

        public void Dispose() => table.Dispose();
    }
}
