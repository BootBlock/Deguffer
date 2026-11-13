namespace Deguffer.Core.Exploring;

/// <summary>
/// What a reader tells <see cref="ExploreTree.Create"/> beside the columns it hands over: what only
/// one route has, and what a caller assembling one tree after another reuses. The default is none of
/// it, for a reader that hands over every slot filled and asks nothing more.
/// </summary>
/// <param name="ListingRefused">
/// The folders the walk was refused a listing of, which <see cref="ExploreTree.ListingWasRefused"/>
/// answers for. A set rather than a column, because a refusal is rare against the millions of nodes
/// a column would spend a slot on each.
/// </param>
/// <param name="NameRanks">
/// Each node's place by name among its siblings, which the walk knows as soon as a folder's children
/// arrive. Given it, a tree ordered <see cref="ExploreChildOrder.ByName"/> places each child in name
/// order as it is linked and is not sorted at all — the sort that, done again on every snapshot of a
/// walk in progress, was most of its cost. The file table reads a folder's children in record order,
/// scattered through the table, so it has no ranks to give, and its snapshots are sorted by name
/// instead. Like the other columns nothing totals into, it may be longer than the node count.
/// </param>
/// <param name="Links">
/// The file table's further names of its hard-linked files, each a node numbered after every slot of
/// the other columns, in columns of their own so the reader never grows those to make room. The nodes
/// kept are copied from both at once. The walk lists a file under each of its names as it meets them,
/// so it has none, and nor has a reader that ranks its names.
/// </param>
/// <param name="Snapshot">Whether the tree is of a scan still running (<see cref="ExploreTree.IsSnapshot"/>).</param>
/// <param name="OrderBuffer">
/// Room for the top-down order of every node, reused by a caller that assembles one tree after
/// another. The tree does not keep it.
/// </param>
/// <param name="DrawnRoot">
/// What the root is drawn by in place of what its slot holds, written after compaction, so it reaches
/// only the columns the tree keeps.
/// </param>
/// <param name="Scratch">
/// Says the columns are a reader's that it is still writing, beside the flags and parents, which are
/// the caller's own. They are then always compacted, so nothing is totalled into them, and read only
/// at the slots the root reaches, which the flags mark present. See <see cref="SnapshotScratch"/>,
/// whose buffers take the place of the arrays compaction replaces.
/// </param>
internal readonly record struct TreeAssemblyOptions(
    IReadOnlyCollection<int>? ListingRefused = null,
    int[]? NameRanks = null,
    MftExploreColumns? Links = null,
    bool Snapshot = false,
    int[]? OrderBuffer = null,
    DrawnRoot? DrawnRoot = null,
    SnapshotScratch? Scratch = null);
