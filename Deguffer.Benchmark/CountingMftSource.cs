using System.Diagnostics;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// A volume's file table that counts what it hands out.
///
/// <para>Counted here rather than worked out from the record count, because a read can stop short:
/// at a region that cannot be read, or where the index abandons the volume. A pass also skips the
/// records <c>$MFT</c>'s <c>$BITMAP</c> marks free. A rate worked out from the size of the table
/// would then report bytes the run never read.</para>
///
/// <para>Counted with interlocked adds, because a pass has several reads in flight and they complete
/// on whatever threads the system completes them on.</para>
/// </summary>
internal sealed class CountingMftSource(IMftSource inner) : IMftSource
{
    private long _recordsRead;
    private long _bytesRead;
    private long _reads;
    private long _clusterReads;
    private long _firstListReadStarted;
    private long _lastListReadEnded;

    public int BytesPerRecord => inner.BytesPerRecord;

    public long RecordCount => inner.RecordCount;

    public MftBitmapPlacement? Bitmap => inner.Bitmap;

    public long RecordsRead => Interlocked.Read(ref _recordsRead);

    /// <summary>
    /// How many reads of records were asked for, including any that returned nothing. Against the
    /// records read it says how large the reads were, which a pass that ends a read at a run of free
    /// records makes smaller and more numerous.
    /// </summary>
    public long Reads => Interlocked.Read(ref _reads);

    /// <summary>
    /// Records and clusters both. A table whose files spill into extension records also reads the
    /// attribute lists kept outside it, and its bitmap is read from clusters too, and those bytes
    /// are as much a part of the run.
    /// </summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>
    /// How many reads of clusters were asked for, including any that failed: the table's bitmap, and
    /// each run of each attribute list kept outside the table.
    /// </summary>
    public long ClusterReads => Interlocked.Read(ref _clusterReads);

    /// <summary>
    /// From the start of the first overlapped read of clusters to the end of the last, or zero where
    /// there was none. Only the reading of the attribute lists kept outside the table reads clusters
    /// overlapped, and it starts once the first pass has ended, so this is the time those reads add
    /// to the pass, apart from the first pass's.
    /// </summary>
    public TimeSpan ListReadTime
    {
        get
        {
            var started = Interlocked.Read(ref _firstListReadStarted);
            return started == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started, Interlocked.Read(ref _lastListReadEnded));
        }
    }

    public int BatchLength(long firstRecord, int capacity) => inner.BatchLength(firstRecord, capacity);

    public int ReadBatch(long firstRecord, Span<byte> destination) => Count(inner.ReadBatch(firstRecord, destination));

    public async ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
        Count(await inner.ReadBatchAsync(firstRecord, destination, ct).ConfigureAwait(false));

    public int BytesPerCluster => inner.BytesPerCluster;

    public bool TryReadClusters(long firstCluster, Span<byte> destination) =>
        CountClusters(inner.TryReadClusters(firstCluster, destination), destination.Length);

    public async ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct)
    {
        Interlocked.CompareExchange(ref _firstListReadStarted, Stopwatch.GetTimestamp(), 0);

        try
        {
            return CountClusters(await inner.TryReadClustersAsync(firstCluster, destination, ct).ConfigureAwait(false), destination.Length);
        }
        finally
        {
            Latest(ref _lastListReadEnded, Stopwatch.GetTimestamp());
        }
    }

    public void Dispose() => inner.Dispose();

    private int Count(int read)
    {
        Interlocked.Increment(ref _reads);

        if (read > 0)
        {
            Interlocked.Add(ref _recordsRead, read);
            Interlocked.Add(ref _bytesRead, (long)read * BytesPerRecord);
        }

        return read;
    }

    private bool CountClusters(bool read, int bytes)
    {
        Interlocked.Increment(ref _clusterReads);

        if (read)
        {
            Interlocked.Add(ref _bytesRead, bytes);
        }

        return read;
    }

    /// <summary>Raise <paramref name="target"/> to <paramref name="value"/>, as reads completing on several threads at once each try to.</summary>
    private static void Latest(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);

        while (value > current && Interlocked.CompareExchange(ref target, value, current) is var seen && seen != current)
        {
            current = seen;
        }
    }
}
