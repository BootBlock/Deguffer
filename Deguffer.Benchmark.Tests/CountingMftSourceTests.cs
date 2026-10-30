using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// A rate is worked out from what was read, not from the size of the table, so a read that stopped
/// short is not credited with bytes it never read.
/// </summary>
public sealed class CountingMftSourceTests
{
    private const int BytesPerRecord = 1024;

    [Fact]
    public void CountsTheRecordsEachReadReturned()
    {
        using var source = new CountingMftSource(Table(records: 10, unreadableFrom: 10));
        var buffer = new byte[4 * BytesPerRecord];

        Assert.Equal(4, source.ReadBatch(0, buffer));
        Assert.Equal(4, source.ReadBatch(4, buffer));
        Assert.Equal(2, source.ReadBatch(8, buffer));

        Assert.Equal(10, source.RecordsRead);
        Assert.Equal(10 * BytesPerRecord, source.BytesRead);
    }

    /// <summary>A pass reads its batches overlapped, so those are counted the same way.</summary>
    [Fact]
    public async Task CountsTheRecordsAnOverlappedReadReturned()
    {
        using var source = new CountingMftSource(Table(records: 10, unreadableFrom: 6));
        var buffer = new byte[4 * BytesPerRecord];

        Assert.Equal(4, await source.ReadBatchAsync(0, buffer, CancellationToken.None));
        Assert.Equal(2, await source.ReadBatchAsync(4, buffer, CancellationToken.None));
        Assert.Equal(0, await source.ReadBatchAsync(6, buffer, CancellationToken.None));

        Assert.Equal(6, source.RecordsRead);
        Assert.Equal(6 * BytesPerRecord, source.BytesRead);
    }

    [Fact]
    public void ARegionThatCannotBeReadAddsNothing()
    {
        using var source = new CountingMftSource(Table(records: 10, unreadableFrom: 3));
        var buffer = new byte[8 * BytesPerRecord];

        Assert.Equal(3, source.ReadBatch(0, buffer));
        Assert.Equal(0, source.ReadBatch(3, buffer));

        Assert.Equal(3, source.RecordsRead);
        Assert.Equal(3 * BytesPerRecord, source.BytesRead);
        Assert.Equal(10, source.RecordCount);
    }

    /// <summary>
    /// Every read asked for is counted, a read that returned nothing too, so the count says how many
    /// reads the pass made and not only how many found records.
    /// </summary>
    [Fact]
    public async Task CountsEveryReadAskedFor()
    {
        using var source = new CountingMftSource(Table(records: 10, unreadableFrom: 6));
        var buffer = new byte[4 * BytesPerRecord];

        source.ReadBatch(0, buffer);
        await source.ReadBatchAsync(4, buffer, CancellationToken.None);
        await source.ReadBatchAsync(6, buffer, CancellationToken.None);
        source.TryReadClusters(0, new byte[4096]);

        Assert.Equal(3, source.Reads);
    }

    /// <summary>
    /// Every read of clusters is counted, a failed one too: the bitmap's, read whole, and each list's,
    /// read overlapped.
    /// </summary>
    [Fact]
    public async Task CountsEveryClusterReadAskedFor()
    {
        using var source = new CountingMftSource(Table(records: 1, unreadableFrom: 1));

        source.TryReadClusters(0, new byte[4096]);
        await source.TryReadClustersAsync(1, new byte[4096], CancellationToken.None);
        await source.TryReadClustersAsync(2, new byte[100], CancellationToken.None);

        Assert.Equal(3, source.ClusterReads);
        Assert.Equal(0, source.Reads);
    }

    /// <summary>
    /// The lists' time runs from the first overlapped read of clusters starting to the last ending.
    /// The bitmap's read, made whole before the first pass, is not one of them, so a table with no
    /// list outside it reports none.
    /// </summary>
    [Fact]
    public async Task TimesTheOverlappedClusterReadsFromTheFirstStartingToTheLastEnding()
    {
        var read = TimeSpan.FromMilliseconds(40);
        using var source = new CountingMftSource(new SlowClusterReads(Table(records: 1, unreadableFrom: 1), read));

        source.TryReadClusters(0, new byte[4096]);
        Assert.Equal(TimeSpan.Zero, source.ListReadTime);

        for (var cluster = 1; cluster <= 4; cluster++)
        {
            await source.TryReadClustersAsync(cluster, new byte[4096], CancellationToken.None);
        }

        // Two reads' worth of four, because a timer can fire up to a 15.6 ms tick early. A span timed
        // from the last read alone is one read's worth.
        Assert.InRange(source.ListReadTime, 2 * read, TimeSpan.FromSeconds(30));
    }

    private static FixtureMftSource Table(int records, long unreadableFrom) =>
        new(
            [.. Enumerable.Range(0, records).Select(_ => new byte[BytesPerRecord])],
            BytesPerRecord,
            unreadableFrom,
            bytesPerCluster: 4096,
            clusters: new Dictionary<long, byte[]>(),
            bitmap: null);

    /// <summary>A table whose overlapped reads of clusters each take <c>read</c>, as a disk's would.</summary>
    private sealed class SlowClusterReads(IMftSource table, TimeSpan read) : IMftSource
    {
        public int BytesPerRecord => table.BytesPerRecord;

        public long RecordCount => table.RecordCount;

        public MftBitmapPlacement? Bitmap => table.Bitmap;

        public int BytesPerCluster => table.BytesPerCluster;

        public int BatchLength(long firstRecord, int capacity) => table.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination) => table.ReadBatch(firstRecord, destination);

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            table.ReadBatchAsync(firstRecord, destination, ct);

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => table.TryReadClusters(firstCluster, destination);

        public async ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct)
        {
            await Task.Delay(read, ct).ConfigureAwait(false);
            return await table.TryReadClustersAsync(firstCluster, destination, ct).ConfigureAwait(false);
        }

        public void Dispose() => table.Dispose();
    }
}
