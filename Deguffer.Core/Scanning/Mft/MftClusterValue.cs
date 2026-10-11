namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Reads the value of an attribute kept outside the table, in clusters of its own: an
/// <c>$ATTRIBUTE_LIST</c> grown too large for its record, or <c>$MFT</c>'s <c>$BITMAP</c>.
/// </summary>
internal static class MftClusterValue
{
    /// <summary>
    /// The first <paramref name="length"/> bytes the runs hold, or null where any part of them
    /// cannot be read. Only the clusters holding those bytes are read: the allocation can run past
    /// them, and a sparse run is a hole where the value should be.
    /// </summary>
    /// <param name="length">At most what the runs cover. A caller sizes it from a field it has bounded.</param>
    public static byte[]? TryRead(ClusterReader read, int clusterBytes, IReadOnlyList<DataRun> runs, int length)
    {
        if (length == 0)
        {
            return [];
        }

        if (Plan(clusterBytes, runs, length) is not { } pieces)
        {
            return null;
        }

        // Aligned because a volume source reads straight into it. See VolumeReadBuffer for why.
        using var buffer = new VolumeReadBuffer(BytesFor(clusterBytes, length));

        foreach (var (cluster, offset, bytes) in pieces)
        {
            if (!read(cluster, buffer.Span.Slice(offset, bytes)))
            {
                return null;
            }
        }

        return buffer.Span[..length].ToArray();
    }

    /// <summary>
    /// The same into <paramref name="clusters"/>, overlapped, so several values can be read at once.
    /// The value is the first <paramref name="length"/> bytes of <paramref name="clusters"/>, and only
    /// where this returns true.
    /// </summary>
    /// <param name="clusters">
    /// At least <see cref="BytesFor"/> bytes of a <see cref="VolumeReadBuffer"/>, held until this has
    /// completed, cancelled or not.
    /// </param>
    public static async Task<bool> TryReadAsync(
        IMftSource source, IReadOnlyList<DataRun> runs, int length, Memory<byte> clusters, CancellationToken ct)
    {
        if (Plan(source.BytesPerCluster, runs, length) is not { } pieces)
        {
            return false;
        }

        // One piece after another rather than all at once: almost every list is one run, and the
        // values are what is read several at a time.
        foreach (var (cluster, offset, bytes) in pieces)
        {
            if (!await source.TryReadClustersAsync(cluster, clusters.Slice(offset, bytes), ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How many bytes of whole clusters hold the first <paramref name="length"/> bytes of a value.</summary>
    public static int BytesFor(int clusterBytes, int length) =>
        // Cannot overflow: every caller bounds the length well below int.MaxValue less a cluster,
        // and a cluster is at most 2 MiB.
        (length + clusterBytes - 1) / clusterBytes * clusterBytes;

    /// <summary>
    /// The reads that bring in the clusters holding the first <paramref name="length"/> bytes the runs
    /// hold, each as a cluster and where its bytes go, or null where a sparse run or the end of the
    /// runs comes first.
    /// </summary>
    private static List<(long Cluster, int Offset, int Bytes)>? Plan(int clusterBytes, IReadOnlyList<DataRun> runs, int length)
    {
        var wanted = BytesFor(clusterBytes, length);
        var pieces = new List<(long Cluster, int Offset, int Bytes)>(runs.Count);
        var filled = 0;

        foreach (var run in runs)
        {
            if (filled == wanted)
            {
                break;
            }

            if (run.IsSparse)
            {
                return null;
            }

            var take = (int)Math.Min(run.ClusterCount, (wanted - filled) / clusterBytes) * clusterBytes;

            pieces.Add((run.StartCluster, filled, take));
            filled += take;
        }

        return filled == wanted ? pieces : null;
    }
}
