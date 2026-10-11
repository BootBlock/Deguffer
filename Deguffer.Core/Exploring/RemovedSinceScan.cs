namespace Deguffer.Core.Exploring;

/// <summary>
/// What has been taken out of a scanned tree since the scan: the items removed, the bytes each
/// folder above them lost, and the child lists that changed. See <see cref="ExploreTree.Without"/>.
///
/// <para>Kept beside the scanned arrays rather than written into copies of them. A removal takes a
/// handful of folders from a tree of millions of nodes, so what changed is the folders above each
/// one and their parents' child lists: a few entries here, against a copy of every size, length
/// and child list for each removal.</para>
/// </summary>
internal sealed class RemovedSinceScan
{
    /// <summary>The items taken out, each with everything under it.</summary>
    private readonly HashSet<int> _gone;

    /// <summary>The bytes on disk each folder above an item taken out has lost.</summary>
    private readonly Dictionary<int, long> _bytes;

    /// <summary>The length each folder above an item taken out has lost.</summary>
    private readonly Dictionary<int, long> _lengths;

    /// <summary>The child lists a removal changed, in the tree's order.</summary>
    private readonly Dictionary<int, int[]> _children;

    private RemovedSinceScan(
        int count,
        HashSet<int> gone,
        Dictionary<int, long> bytes,
        Dictionary<int, long> lengths,
        Dictionary<int, int[]> children)
    {
        Count = count;
        _gone = gone;
        _bytes = bytes;
        _lengths = lengths;
        _children = children;
    }

    /// <summary>How many items have been removed, counted as they were picked.</summary>
    public int Count { get; }

    public long BytesLost(int node) => _bytes.GetValueOrDefault(node);

    public long LengthLost(int node) => _lengths.GetValueOrDefault(node);

    /// <summary>The node's children less what was taken out, where a removal changed them.</summary>
    public bool TryChildren(int node, out int[] children) => _children.TryGetValue(node, out children!);

    /// <summary>Whether <paramref name="node"/> was taken out, or is under something that was.</summary>
    public bool Covers(int node, Func<int, int> parentOf)
    {
        for (var current = node; ; )
        {
            if (_gone.Contains(current))
            {
                return true;
            }

            var parent = parentOf(current);

            // The root is its own parent, and so is a node the tree cannot place.
            if (parent == current)
            {
                return false;
            }

            current = parent;
        }
    }

    /// <summary>
    /// What is taken out of <paramref name="tree"/> once <paramref name="removed"/> are too, or null
    /// where none of them is still in it.
    ///
    /// <para>The root stays, as the place the scan was of, and taking it out takes everything under
    /// it. Every other node goes, and the folders above it lose its size and length. Each changed
    /// child list is put back in the tree's order, because a folder that lost bytes can be smaller
    /// now than a sibling it was drawn ahead of, and the treemap and the sunburst draw only a list
    /// ordered by size (<see cref="ExploreChildOrder"/>).</para>
    /// </summary>
    /// <param name="tree">The tree as it stands, which may have had items taken out already.</param>
    /// <param name="earlier">What had been taken out of it already, or null.</param>
    public static RemovedSinceScan? Taking(ExploreTree tree, RemovedSinceScan? earlier, IEnumerable<int> removed)
    {
        var picked = removed.Where(tree.Holds).ToHashSet();

        if (picked.Count == 0)
        {
            return null;
        }

        var root = tree.RootNode;
        HashSet<int> cut = picked.Contains(root) ? [.. tree.ChildrenOf(root)] : [.. picked];

        // An item inside another taken out with it has gone already, and its bytes with it.
        cut.RemoveWhere(node => Inside(tree, cut, node));

        // What this removal takes from each folder above it, apart from what earlier ones took, which
        // the tree's sizes already leave out.
        var bytesNow = new Dictionary<int, long>();
        var lengthsNow = new Dictionary<int, long>();
        var reordered = new HashSet<int>();

        foreach (var node in cut)
        {
            var size = tree.SizeOf(node);
            var length = tree.LengthOf(node);

            reordered.Add(tree.ParentOf(node));

            for (var above = tree.ParentOf(node); ; above = tree.ParentOf(above))
            {
                bytesNow[above] = bytesNow.GetValueOrDefault(above) + size;
                lengthsNow[above] = lengthsNow.GetValueOrDefault(above) + length;

                if (above == root)
                {
                    break;
                }

                // Smaller now, so it may stand somewhere else among its siblings.
                reordered.Add(tree.ParentOf(above));
            }
        }

        var children = new Dictionary<int, int[]>(earlier?._children ?? []);
        Comparison<int> byName = tree.CompareByName;

        foreach (var folder in reordered)
        {
            var kept = tree.ChildrenOf(folder).ToArray().Where(child => !cut.Contains(child)).ToArray();

            if (tree.ChildOrder == ExploreChildOrder.BySize)
            {
                ExploreTree.OrderBySize(kept, SizeAfter, byName);
            }

            children[folder] = kept;
        }

        return new RemovedSinceScan(
            (earlier?.Count ?? 0) + picked.Count,
            [.. earlier?._gone ?? [], .. cut],
            Added(earlier?._bytes, bytesNow),
            Added(earlier?._lengths, lengthsNow),
            children);

        long SizeAfter(int node) => tree.SizeOf(node) - bytesNow.GetValueOrDefault(node);
    }

    private static Dictionary<int, long> Added(Dictionary<int, long>? earlier, Dictionary<int, long> now)
    {
        var sum = new Dictionary<int, long>(earlier ?? []);

        foreach (var (folder, lost) in now)
        {
            sum[folder] = sum.GetValueOrDefault(folder) + lost;
        }

        return sum;
    }

    private static bool Inside(ExploreTree tree, HashSet<int> cut, int node)
    {
        for (var above = tree.ParentOf(node); ; above = tree.ParentOf(above))
        {
            if (cut.Contains(above))
            {
                return true;
            }

            if (above == tree.RootNode)
            {
                return false;
            }
        }
    }
}
