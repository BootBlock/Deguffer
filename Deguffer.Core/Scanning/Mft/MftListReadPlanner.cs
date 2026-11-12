namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One read of the attribute lists kept outside the table: lists <see cref="First"/> up to
/// <see cref="First"/> plus <see cref="Count"/>, in the order they were planned.
///
/// <para><see cref="Cluster"/> is where the first list's first run starts. A read of one list reads it
/// by its own runs, whatever they are, into <see cref="Bytes"/>. A read of several reads
/// <see cref="Bytes"/> from <see cref="Cluster"/> in one piece, and each list is the slice of it its
/// first cluster begins.</para>
/// </summary>
internal readonly record struct MftListRead(int First, int Count, long Cluster, int Bytes);

/// <summary>
/// Decides which of the attribute lists kept outside the table are read together, before any of them
/// is read, so several reads can be outstanding at once.
///
/// <para>On a real volume almost every list is one cluster, and about half sit in the cluster right
/// after the one before, so a read each costs the disk a request for every cluster. Lists in cluster
/// order whose clusters touch, or are apart by no more than <see cref="MftReadGap"/>, are read as one,
/// for the reason the passes over the table read across a short run of records nobody wants: one read
/// costs less than two either side of a short gap.</para>
///
/// <para>A read of several is at most <see cref="MftAttributeList.MaximumLength"/>, the largest a
/// single list can be, so the buffer a read needs is no larger than one list could already ask for
/// (see <see cref="TableTuning"/>). A list whose clusters are not all in its first run, or are larger
/// than that on their own, is read alone, as every list was before.</para>
/// </summary>
internal static class MftListReadPlanner
{
    /// <summary>The reads that bring in every one of <paramref name="lists"/>, which are in the order of their first clusters.</summary>
    /// <param name="lists">Each list's runs, at least one, and how many bytes of them are the list.</param>
    public static IReadOnlyList<MftListRead> Plan(IReadOnlyList<(IReadOnlyList<DataRun> Runs, int Length)> lists, int clusterBytes)
    {
        long gapClusters = MftReadGap.Bytes / clusterBytes;
        long mostClusters = MftAttributeList.MaximumLength / clusterBytes;
        var reads = new List<MftListRead>();
        var next = 0;

        while (next < lists.Count)
        {
            var first = next++;

            if (!TryExtent(lists[first], clusterBytes, out var start, out var end) || end - start > mostClusters)
            {
                reads.Add(new MftListRead(first, 1, start, MftClusterValue.BytesFor(clusterBytes, lists[first].Length)));
                continue;
            }

            // A list starting before the end of the one before overlaps it, which only damage does.
            // It is still read whole, from its own slice, as it was read whole on its own.
            while (next < lists.Count
                   && TryExtent(lists[next], clusterBytes, out var nextStart, out var nextEnd)
                   && nextStart - end <= gapClusters
                   && Math.Max(end, nextEnd) - start <= mostClusters)
            {
                end = Math.Max(end, nextEnd);
                next++;
            }

            reads.Add(new MftListRead(first, next - first, start, (int)(end - start) * clusterBytes));
        }

        return reads;
    }

    /// <summary>
    /// The clusters holding a list, from <paramref name="start"/> up to <paramref name="end"/>, where
    /// they are all in its first run and so can be read as one piece.
    /// </summary>
    private static bool TryExtent(
        (IReadOnlyList<DataRun> Runs, int Length) list, int clusterBytes, out long start, out long end)
    {
        var first = list.Runs[0];
        long clusters = MftClusterValue.BytesFor(clusterBytes, list.Length) / clusterBytes;

        start = first.StartCluster;

        // A run's start comes off the disk, and one near the largest cluster number would wrap the end
        // negative and pass every test below. Read alone, it fails as any read past the volume does.
        if (first.IsSparse || first.ClusterCount < clusters || start > long.MaxValue - clusters)
        {
            end = start;
            return false;
        }

        end = start + clusters;
        return true;
    }
}
