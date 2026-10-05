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

    public int BytesPerRecord => inner.BytesPerRecord;

    public long RecordCount => inner.RecordCount;

    public MftBitmapPlacement? Bitmap => inner.Bitmap;

    public long RecordsRead => Interlocked.Read(ref _recordsRead);

    /// <summary>
    /// Records and clusters both. A table whose files spill into extension records also reads the
    /// attribute lists kept outside it, and its bitmap is read from clusters too, and those bytes
    /// are as much a part of the run.
    /// </summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public int BatchLength(long firstRecord, int capacity) => inner.BatchLength(firstRecord, capacity);

    public int ReadBatch(long firstRecord, Span<byte> destination) => Count(inner.ReadBatch(firstRecord, destination));

    public async ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
        Count(await inner.ReadBatchAsync(firstRecord, destination, ct).ConfigureAwait(false));

    public int BytesPerCluster => inner.BytesPerCluster;

    public bool TryReadClusters(long firstCluster, Span<byte> destination)
    {
        var read = inner.TryReadClusters(firstCluster, destination);

        if (read)
        {
            Interlocked.Add(ref _bytesRead, destination.Length);
        }

        return read;
    }

    public void Dispose() => inner.Dispose();

    private int Count(int read)
    {
        if (read > 0)
        {
            Interlocked.Add(ref _recordsRead, read);
            Interlocked.Add(ref _bytesRead, (long)read * BytesPerRecord);
        }

        return read;
    }
}
