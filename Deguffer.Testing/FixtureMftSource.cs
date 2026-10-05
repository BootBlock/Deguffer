using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>
/// Serves records built by <see cref="MftFixture"/>, and can refuse to serve them from a chosen
/// point on — standing in for a bad sector or a run list the reader could not follow.
///
/// <para>Also serves the few clusters outside the table a fixture placed something in. Any other
/// cluster cannot be read, so a reader that goes looking in the wrong place fails rather than
/// finding zeroes that happen to parse.</para>
///
/// <para>Every read completes before it returns, so reads complete in the order they were made. A
/// test that needs them to complete otherwise wraps this source.</para>
/// </summary>
public sealed class FixtureMftSource(
    IReadOnlyList<byte[]> records,
    int bytesPerRecord,
    long unreadableFrom,
    int bytesPerCluster,
    IReadOnlyDictionary<long, byte[]> clusters,
    MftBitmapPlacement? bitmap) : IMftSource
{
    public int BytesPerRecord => bytesPerRecord;

    public MftBitmapPlacement? Bitmap => bitmap;

    /// <summary>
    /// What the table holds from the record on, up to the room given. A read past where the table
    /// stops being readable is still planned, and then reads short, as a bad sector does.
    /// </summary>
    public int BatchLength(long firstRecord, int capacity) =>
        firstRecord < 0 || firstRecord >= records.Count ? 0 : (int)Math.Min(capacity, records.Count - firstRecord);

    public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
        ValueTask.FromResult(ReadBatch(firstRecord, destination.Span));

    public int BytesPerCluster => bytesPerCluster;

    public long RecordCount => records.Count;

    public int ReadBatch(long firstRecord, Span<byte> destination)
    {
        if (firstRecord >= unreadableFrom)
        {
            return 0;
        }

        var capacity = destination.Length / bytesPerRecord;
        var available = (int)Math.Min(capacity, Math.Min(records.Count, unreadableFrom) - firstRecord);

        for (var i = 0; i < available; i++)
        {
            records[(int)firstRecord + i].CopyTo(destination[(i * bytesPerRecord)..]);
        }

        return Math.Max(0, available);
    }

    public bool TryReadClusters(long firstCluster, Span<byte> destination)
    {
        if (destination.Length % bytesPerCluster != 0)
        {
            return false;
        }

        for (var i = 0; i < destination.Length / bytesPerCluster; i++)
        {
            if (!clusters.TryGetValue(firstCluster + i, out var cluster))
            {
                return false;
            }

            cluster.CopyTo(destination[(i * bytesPerCluster)..]);
        }

        return true;
    }

    public void Dispose()
    {
    }
}
