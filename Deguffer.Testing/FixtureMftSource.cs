using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>
/// Serves records built by <see cref="MftFixture"/>, and can refuse to serve them from a chosen
/// point on — standing in for a bad sector or a run list the reader could not follow.
///
/// <para>Also serves the few clusters outside the table a fixture placed something in. Any other
/// cluster cannot be read, so a reader that goes looking in the wrong place fails rather than
/// finding zeroes that happen to parse.</para>
/// </summary>
public sealed class FixtureMftSource(
    IReadOnlyList<byte[]> records,
    int bytesPerSector,
    int bytesPerRecord,
    long unreadableFrom,
    int bytesPerCluster,
    IReadOnlyDictionary<long, byte[]> clusters) : IMftSource
{
    public int BytesPerSector => bytesPerSector;

    public int BytesPerRecord => bytesPerRecord;

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
