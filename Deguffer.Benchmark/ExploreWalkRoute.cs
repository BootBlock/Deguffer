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
    public static RunTally Run(string folder, WalkTuning tuning, bool snapshots, CancellationToken ct)
    {
        // The snapshots are assembled and dropped, as the reports a person never sees would be. Each
        // is still a whole pass over the tree so far, which is the cost being timed.
        var tree = WalkExploreReader.Read(
            folder,
            tuning,
            OccupancyProbe.Default,
            new Discarded(),
            TimeProvider.System,
            ct,
            assembleSnapshot: snapshots ? WalkSnapshots.InBackground : null);

        // A folder the walk was refused, or a file it could not measure, leaves the root's size
        // unknown, and the run timed less than the folder holds.
        return new RunTally(tree.NodeCount - 1, BytesRead: 0, Complete: !tree.HasUnknownSizes, Kept: tree);
    }

    private sealed class Discarded : IProgress<ExploreProgress>
    {
        public void Report(ExploreProgress value)
        {
        }
    }
}
