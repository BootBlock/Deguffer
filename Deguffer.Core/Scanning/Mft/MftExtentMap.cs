namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Where the MFT's own records physically live, as <see cref="MftExtentMapReader"/> reads it from
/// <c>$MFT</c>'s own records.
///
/// The table is not necessarily contiguous. A reader that assumes it is will, on any volume whose
/// MFT has ever grown, read the right number of bytes from the wrong place and produce records that
/// parse cleanly and describe nothing.
/// </summary>
public sealed record MftExtentMap(long DataSize, IReadOnlyList<DataRun> Runs)
{
    /// <summary>
    /// Translate a virtual cluster within the MFT stream to its physical cluster, and report how
    /// many clusters remain contiguous from there.
    ///
    /// The contiguous count is what lets the caller read in large batches without straddling an
    /// extent boundary — reading across one would splice unrelated regions of the disk into what
    /// looks like a run of consecutive records.
    /// </summary>
    public bool TryTranslate(long virtualCluster, out long physicalCluster, out long contiguousClusters)
    {
        physicalCluster = 0;
        contiguousClusters = 0;

        long seen = 0;

        foreach (var run in Runs)
        {
            if (virtualCluster < seen + run.ClusterCount)
            {
                // A sparse run occupies this position but holds nothing to read. Refusing is the
                // only honest answer: reporting the next run's clusters here would return real data
                // from the wrong offset.
                if (run.IsSparse)
                {
                    return false;
                }

                var into = virtualCluster - seen;
                physicalCluster = run.StartCluster + into;
                contiguousClusters = run.ClusterCount - into;
                return true;
            }

            // Sparse runs are counted here as well as elsewhere: virtual cluster numbers are
            // positional, so skipping one would shift every later run onto the wrong disk offset.
            seen += run.ClusterCount;
        }

        return false;
    }

    /// <summary>
    /// Read record <paramref name="number"/> of the table into <paramref name="destination"/>,
    /// one record long. False where any of its clusters lies outside these extents, or a read fails.
    ///
    /// <para>Read in whole clusters, as a raw volume read must be, and assembled from as many
    /// extents as the record spans. On a volume whose clusters are smaller than its records, a
    /// record can begin in one extent and end in another, or in one past that.</para>
    /// </summary>
    internal bool TryReadRecord(long number, int bytesPerCluster, ClusterReader read, Span<byte> destination)
    {
        var bytesPerRecord = destination.Length;

        // Bounded by division before the offset is formed: the number can come off the disk, and a
        // record past the table's end is not one of its records.
        if (number < 0 || number >= DataSize / bytesPerRecord)
        {
            return false;
        }

        var offset = number * bytesPerRecord;
        var firstCluster = offset / bytesPerCluster;
        var clusterCount = (int)(((offset + bytesPerRecord - 1) / bytesPerCluster) - firstCluster + 1);

        // Aligned because a volume source reads straight into it. See VolumeReadBuffer for why.
        using var buffer = new VolumeReadBuffer(clusterCount * bytesPerCluster);
        var filled = 0;

        while (filled < clusterCount)
        {
            if (!TryTranslate(firstCluster + filled, out var physical, out var contiguous))
            {
                return false;
            }

            var take = (int)Math.Min(contiguous, clusterCount - filled);

            if (!read(physical, buffer.Span.Slice(filled * bytesPerCluster, take * bytesPerCluster)))
            {
                return false;
            }

            filled += take;
        }

        buffer.Span.Slice((int)(offset - (firstCluster * bytesPerCluster)), bytesPerRecord).CopyTo(destination);
        return true;
    }
}
