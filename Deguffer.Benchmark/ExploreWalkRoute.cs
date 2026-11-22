using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark;

/// <summary>
/// The Explore tree for one folder, built by walking it as an unelevated Explore does, with or
/// without the pictures of the tree so far that Explore draws while it walks.
///
/// <para>Beside <see cref="WalkRoute"/> rather than in place of it. The bare walk times the listing
/// alone, and this times what Explore adds to it: recording every entry, the snapshots, and the
/// finished tree. Run once with snapshots and once without, the difference is what the snapshots
/// cost the walk.</para>
/// </summary>
internal static class ExploreWalkRoute
{
    /// <param name="snapshots">The snapshots the walk assembles and times, or null to take none.</param>
    public static RunTally Run(string folder, WalkTuning tuning, TimedSnapshots? snapshots, CancellationToken ct)
    {
        // Each snapshot is a whole pass over the tree so far, which is the cost being timed.
        var tree = WalkExploreReader.Read(
            folder,
            tuning,
            OccupancyProbe.Default,
            DiscardedProgress.Instance,
            TimeProvider.System,
            ct,
            snapshotAssembly: snapshots?.Assembly);

        // A folder the walk was refused, or a file it could not measure, leaves the root's size
        // unknown, and the run timed less than the folder holds.
        return new RunTally(
            tree.NodeCount - 1, BytesRead: 0, Complete: !tree.HasUnknownSizes, Kept: new KeptResult(tree), Snapshots: snapshots?.Times);
    }
}
