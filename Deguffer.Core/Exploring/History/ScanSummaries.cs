namespace Deguffer.Core.Exploring.History;

/// <summary>
/// Takes a <see cref="ScanSummary"/> from a finished scan of a whole volume.
///
/// <para><b>From the tree the scan already built, never from the disk.</b> G4: a second walk would
/// cost as much as the scan did, and would describe a disk a moment later than the picture on
/// screen.</para>
/// </summary>
public static class ScanSummaries
{
    /// <summary>
    /// How many folders a summary records.
    ///
    /// <para>A count rather than a depth or a fixed size floor, because either of those is a
    /// different number of folders on every disk. A depth of four is a few hundred folders on a data
    /// drive and tens of thousands on a system drive, where the component store alone holds that
    /// many. A count bounds what is stored, and the floor it implies is recorded with it.</para>
    ///
    /// <para>Twenty thousand is a bound on what is stored, a few hundred kilobytes a summary, rather
    /// than a measured reach. How far down it reaches on a given volume is that summary's
    /// <see cref="ScanSummary.UnrecordedAtMost"/>, and the comparison reads it from there.</para>
    /// </summary>
    public const int FolderLimit = 20_000;

    /// <summary>Larger sizes first, for the queue <see cref="Take"/> draws from (G5).</summary>
    private static readonly Comparer<long> Largest =
        Comparer<long>.Create(static (left, right) => right.CompareTo(left));

    /// <summary>
    /// The summary of <paramref name="scan"/>, a scan of the whole of the volume named
    /// <paramref name="volume"/>, which had <paramref name="space"/> when it finished.
    /// </summary>
    public static ScanSummary Take(
        ExploreScan scan, string volume, VolumeSpace space, DateTime takenUtc, int limit = FolderLimit)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var tree = scan.Tree;
        var folders = new List<SummaryFolder>(Math.Min(limit, 1024));

        // Largest first, across the whole tree rather than level by level. A child enters the queue
        // only once its parent has been recorded, so the record stays closed under "the folder
        // holding this one" whatever order equal sizes leave in.
        var waiting = new PriorityQueue<(int Node, int Parent), long>(Largest);

        waiting.Enqueue((tree.RootNode, -1), tree.SizeOf(tree.RootNode));

        while (folders.Count < limit && waiting.TryDequeue(out var next, out var bytes))
        {
            var position = folders.Count;
            folders.Add(new SummaryFolder(
                next.Parent,
                next.Parent < 0 ? tree.RootPath : tree.NameOf(next.Node),
                bytes));

            foreach (var child in tree.ChildrenOf(next.Node))
            {
                // A link holds nothing here (ExploreTree.IsLink), and its target is recorded where it
                // really is. A file is not a folder, and recording one would spend the limit on it.
                if (tree.IsDirectory(child) && !tree.IsLink(child))
                {
                    waiting.Enqueue((child, position), tree.SizeOf(child));
                }
            }
        }

        // What the largest folder left out held, which bounds every folder left out: each one is
        // either still waiting or below one that is.
        var unrecordedAtMost = waiting.TryPeek(out _, out var largest) ? largest : 0;

        return new ScanSummary(
            volume,
            tree.RootPath,
            takenUtc,
            scan.Strategy,
            tree.HasUnknownSizes,
            space.TotalBytes,
            space.FreeBytes,
            unrecordedAtMost,
            folders);
    }
}
