using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// Several folders scanned as a duplicate search scans them, through
/// <see cref="ExploreScanner.ScanFoldersAsync(IReadOnlyList{string}, IProgress{ExploreProgress}?, CancellationToken)"/>:
/// each volume's file table read once where it can be, the walk where it cannot, and the volumes on
/// separate disks at the same time.
///
/// <para>The tuner asks each volume's device which disks it is on and what kind they are, as the
/// app's does, so each volume is read or walked with the values a search would use on it, under the
/// shipped settings.</para>
/// </summary>
internal static class FoldersRoute
{
    /// <param name="sources">What opens each volume's file table: the machine's, or a test's.</param>
    public static RunTally Run(IMftSourceFactory sources, IReadOnlyList<string> folders, CancellationToken ct)
    {
        var tuner = new ScanTuner(DefaultPreferences.Instance, new VolumeMediaCache(StorageQueries.Machine), VolumeInventory.Current);
        var counted = new LastCount();
        var scans = new ExploreScanner(sources, tuning: tuner).ScanFoldersAsync(folders, counted, ct).AsTask().GetAwaiter().GetResult();

        // A table not read whole, or a folder the walk was refused, is a run that read less than the
        // folders hold.
        return new RunTally(
            counted.Done,
            BytesRead: 0,
            Complete: scans.All(scan => !scan.FromIncompleteTable && !scan.Tree.HasUnknownSizeBelow(scan.Node)));
    }

    /// <summary>
    /// The scan's last count: the records its table reads read and the entries its walks listed, every
    /// pass added together. The scan reports them in the order it adds them up.
    /// </summary>
    private sealed class LastCount : IProgress<ExploreProgress>
    {
        public long Done { get; private set; }

        public void Report(ExploreProgress value) => Done = value.Done;
    }
}
