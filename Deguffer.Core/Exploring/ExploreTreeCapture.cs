using System.Runtime.InteropServices;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What an <see cref="ExploreTreeBuilder"/> held at one moment, assembled into an
/// <see cref="ExploreTree"/> without the builder's lock.
///
/// <para>Apart from the builder because taking a capture and assembling one happen at different
/// times and on different threads: the capture is taken under the lock every walk worker needs,
/// and the assembling is a pass over every node that no worker should wait for.</para>
/// </summary>
internal sealed class ExploreTreeCapture
{
    // The token is asked once every 65,536 nodes copied: often enough that a cancelled copy stops
    // at once, and rarely enough to cost nothing beside the copy.
    private const int CancellationMask = (1 << 16) - 1;

    private readonly string _rootPath;
    private readonly AppendOnlyChunks<ExploreTreeBuilder.Node>.Prefix _nodes;
    private readonly NamePool.Frozen _names;
    private readonly int[] _sizeUnknown;
    private readonly int[] _listingRefused;
    private readonly int[] _listedTwice;

    /// <param name="sizeUnknown">Nodes marked as unmeasured after they were added.</param>
    /// <param name="listingRefused">Folders the walk was refused a listing of.</param>
    /// <param name="listedTwice">
    /// Parents whose children were recorded in more than one call, so whose name ranks are each
    /// call's own and have to be worked out again across all of them.
    /// </param>
    public ExploreTreeCapture(
        string rootPath,
        AppendOnlyChunks<ExploreTreeBuilder.Node>.Prefix nodes,
        NamePool.Frozen names,
        int[] sizeUnknown,
        int[] listingRefused,
        int[] listedTwice)
    {
        _rootPath = rootPath;
        _nodes = nodes;
        _names = names;
        _sizeUnknown = sizeUnknown;
        _listingRefused = listingRefused;
        _listedTwice = listedTwice;
    }

    /// <summary>
    /// The tree, ordering each node's children by <paramref name="childOrder"/>, in arrays of its own
    /// sized to the tree, because it is the tree a finished walk keeps.
    ///
    /// <para>Every slot was filled by the builder, so every one is marked
    /// <see cref="ExploreNodeFlags.Present"/> — the distinction <see cref="ExploreTree.Create"/>
    /// needs exists for the file table, which leaves free and unreadable records empty.</para>
    /// </summary>
    /// <param name="ct">
    /// Asked every so many nodes and between the passes, so a snapshot of a walk that has ended or
    /// been cancelled stops part way rather than holding the walk until it is finished.
    /// </param>
    public ExploreTree Build(ExploreChildOrder childOrder, CancellationToken ct = default) =>
        Build(childOrder, new SnapshotColumns(), snapshot: false, ct);

    /// <summary>
    /// The tree as a snapshot of a walk in progress draws it: <see cref="Build(ExploreChildOrder, CancellationToken)"/>,
    /// marked as a snapshot (<see cref="ExploreTree.IsSnapshot"/>), in the order
    /// <see cref="ScanSnapshots"/> was asked for when it was captured.
    /// </summary>
    /// <param name="shared">
    /// The columns every snapshot of this walk reads, which this fills for the nodes added since the
    /// last. See <see cref="SnapshotColumns"/>.
    /// </param>
    public ExploreTree Snapshot(ExploreChildOrder childOrder, SnapshotColumns shared, CancellationToken ct) =>
        Build(childOrder, shared, snapshot: true, ct);

    private ExploreTree Build(ExploreChildOrder childOrder, SnapshotColumns shared, bool snapshot, CancellationToken ct)
    {
        var count = _nodes.Count;

        shared.Fill(_nodes, CancellationMask, ct);

        var sizes = new long[count];
        var lengths = new long[count];
        var storage = new FileStorage[count];
        var flags = new ExploreNodeFlags[count];
        var modified = new ExploreTimestamp[count];

        for (var i = 0; i < count; i++)
        {
            if ((i & CancellationMask) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            // By reference, field by field, so that HasChildren, which the builder may still be
            // writing, is never read.
            ref readonly var node = ref _nodes[i];

            sizes[i] = node.Size;
            lengths[i] = node.Length;
            storage[i] = node.Storage;
            flags[i] = ExploreNodeFlags.Present
                | (node.IsDirectory ? ExploreNodeFlags.Directory : ExploreNodeFlags.None)
                | (node.IsLink ? ExploreNodeFlags.Link : ExploreNodeFlags.None)
                | (node.SizeUnknown ? ExploreNodeFlags.SizeUnknown : ExploreNodeFlags.None);
            modified[i] = node.Modified;
        }

        foreach (var node in _sizeUnknown)
        {
            flags[node] |= ExploreNodeFlags.SizeUnknown;
        }

        if (_listedTwice.Length > 0)
        {
            RankAcrossCalls(_listedTwice, count, shared.Parents, shared.NameNumbers, _names, shared.NameRanks);
        }

        return ExploreTree.Create(
            _rootPath,
            ExploreTreeBuilder.RootNode,
            shared.NameNumbers,
            _names,
            shared.Parents,
            sizes,
            lengths,
            storage,
            flags,
            shared.Created,
            modified,
            shared.Visibility,
            childOrder,
            new TreeAssemblyOptions(
                ListingRefused: _listingRefused,
                NameRanks: shared.NameRanks,
                Snapshot: snapshot,
                OrderBuffer: shared.Order),
            ct);
    }

    /// <summary>
    /// Rank by name every child of each of <paramref name="listedTwice"/> among all of its
    /// siblings, not only those recorded in the same call. Two calls' ranks for one parent both
    /// start at zero, and placed as they are, one child would take another's place in the tree.
    /// </summary>
    /// <param name="count">How many nodes the tree holds, which the columns can outnumber.</param>
    private static void RankAcrossCalls(
        int[] listedTwice, int count, int[] parents, int[] nameNumbers, NamePool.Frozen names, int[] nameRanks)
    {
        var siblings = listedTwice.ToDictionary(parent => parent, _ => new List<int>());

        // From one, because the root is its own parent and nobody's child.
        for (var node = 1; node < count; node++)
        {
            if (siblings.TryGetValue(parents[node], out var children))
            {
                children.Add(node);
            }
        }

        foreach (var children in siblings.Values)
        {
            var ordered = CollectionsMarshal.AsSpan(children);
            ordered.Sort((left, right) => ExploreTree.CompareByName(nameNumbers, names, left, right));

            for (var rank = 0; rank < ordered.Length; rank++)
            {
                nameRanks[ordered[rank]] = rank;
            }
        }
    }
}
