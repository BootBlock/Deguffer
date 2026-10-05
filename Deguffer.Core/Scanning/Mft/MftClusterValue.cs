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

        // Cannot overflow: every caller bounds the length well below int.MaxValue less a cluster,
        // and a cluster is at most 2 MiB.
        var wanted = (length + clusterBytes - 1) / clusterBytes * clusterBytes;

        // Aligned because a volume source reads straight into it. See VolumeReadBuffer for why.
        using var buffer = new VolumeReadBuffer(wanted);
        var clusters = buffer.Span;
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

            if (!read(run.StartCluster, clusters.Slice(filled, take)))
            {
                return null;
            }

            filled += take;
        }

        return filled == wanted ? clusters[..length].ToArray() : null;
    }
}
