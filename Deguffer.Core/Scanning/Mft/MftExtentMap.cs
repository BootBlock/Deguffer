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
}
