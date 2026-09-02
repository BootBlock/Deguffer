namespace Deguffer.Core.Exploring;

/// <summary>
/// A scanned tree of directories and files, as parallel arrays indexed by node.
///
/// <para>Parallel arrays for the reason <see cref="Scanning.Mft.MftVolumeTree"/> uses them: a real
/// volume runs to millions of entries, and one object per node would cost more in object headers
/// than every field here put together.</para>
///
/// <para>This holds no knowledge of drawing, of tiers, or of what may be deleted. It answers "what
/// is under this node and how big is it", and nothing else — which is what lets one tree serve a
/// treemap, a sunburst and a list without any of them being privileged.</para>
/// </summary>
public sealed class ExploreTree
{
    private readonly string[] _names;
    private readonly int[] _parents;
    private readonly long[] _sizes;
    private readonly bool[] _isDirectory;
    private readonly bool[] _isLink;
    private readonly bool[] _sizeUnknown;
    private readonly int[] _childStart;
    private readonly int[] _children;

    private ExploreTree(
        string rootPath,
        int rootNode,
        string[] names,
        int[] parents,
        long[] sizes,
        bool[] isDirectory,
        bool[] isLink,
        bool[] sizeUnknown,
        int[] childStart,
        int[] children)
    {
        RootPath = rootPath;
        RootNode = rootNode;
        _names = names;
        _parents = parents;
        _sizes = sizes;
        _isDirectory = isDirectory;
        _isLink = isLink;
        _sizeUnknown = sizeUnknown;
        _childStart = childStart;
        _children = children;
    }

    /// <summary>Where the scan started, as the user picked it — <c>C:\</c>.</summary>
    public string RootPath { get; }

    /// <summary>
    /// The node <see cref="RootPath"/> names. Not always zero: the master file table addresses its
    /// root by a fixed record number, and remapping every node to make it zero would cost a pass
    /// over the whole volume to save an assumption nothing needs.
    /// </summary>
    public int RootNode { get; }

    /// <summary>Total bytes under the root, as far as the scan established them.</summary>
    public long TotalBytes => _sizes[RootNode];

    /// <summary>
    /// Whether any node has a size the scan could not establish, which makes every total above it
    /// a lower bound rather than a measurement.
    ///
    /// <para>Reported rather than refused, and that is the opposite of what the deletion path does.
    /// <see cref="Scanning.Mft.MftVolumeIndex.TryMeasure"/> answers null rather than a total that is
    /// short, because a number that decides a deletion has to be right. Nothing here decides a
    /// deletion, so the trade goes the other way: a picture complete apart from a handful of
    /// records, saying so, beats no picture at all.</para>
    /// </summary>
    public bool HasUnknownSizes => _sizeUnknown[RootNode];

    public int NodeCount => _names.Length;

    public string NameOf(int node) => _names[node];

    public long SizeOf(int node) => _sizes[node];

    public bool IsDirectory(int node) => _isDirectory[node];

    /// <summary>
    /// Whether this node is a junction, a symbolic link or another name surrogate. Its target keeps
    /// its own place in the tree, so a link holds nothing here however much its path appears to.
    /// </summary>
    public bool IsLink(int node) => _isLink[node];

    /// <summary>Whether <see cref="SizeOf"/> for this node is a lower bound.</summary>
    public bool HasUnknownSizeBelow(int node) => _sizeUnknown[node];

    public int ParentOf(int node) => _parents[node];

    /// <summary>
    /// This node's children, largest first. The order is fixed at build time because every consumer
    /// wants it — a treemap needs descending size for its row packing, a list shows it directly,
    /// and a sunburst draws its arcs in it — so sorting once beats sorting per repaint (G4/G5).
    /// </summary>
    public ReadOnlySpan<int> ChildrenOf(int node) =>
        _children.AsSpan(_childStart[node], _childStart[node + 1] - _childStart[node]);

    /// <summary>
    /// The full path of <paramref name="node"/>, rebuilt by walking to the root.
    ///
    /// <para>Rebuilt rather than stored: a path per node would cost more than every other array
    /// here combined, and the UI asks for one only when the user points at something.</para>
    /// </summary>
    public string PathOf(int node)
    {
        if (node == RootNode)
        {
            return RootPath;
        }

        var components = new List<string>();

        for (var current = node; current != RootNode; current = _parents[current])
        {
            components.Add(_names[current]);
        }

        components.Reverse();

        return Path.Combine([RootPath, .. components]);
    }

    /// <summary>
    /// Assemble a tree from the arrays a reader produced: invert the parent links, total each
    /// subtree, and order every child list by size.
    ///
    /// <para>Shared by both routes on purpose. The master file table and the directory walk gather
    /// entirely differently, but what they gather is the same shape, and these three steps are
    /// where a mistake would be invisible in the picture rather than obvious in it.</para>
    ///
    /// <paramref name="present"/> says which slots the reader actually filled. The walk fills every
    /// one; the table leaves free and unreadable records empty, and without this those read as a
    /// forest of node-zero children that would attach the whole volume to the root a second time.
    /// </summary>
    internal static ExploreTree Create(
        string rootPath,
        int rootNode,
        string[] names,
        int[] parents,
        long[] sizes,
        bool[] isDirectory,
        bool[] isLink,
        bool[] sizeUnknown,
        bool[] present)
    {
        var (childStart, children) = InvertParentLinks(parents, present, rootNode);
        var order = DepthFirstOrder(childStart, children, rootNode);

        RollUp(order, parents, sizes, sizeUnknown, rootNode);
        SortChildrenBySize(childStart, children, sizes);

        return new ExploreTree(
            rootPath, rootNode, names, parents, sizes, isDirectory, isLink, sizeUnknown,
            childStart, children);
    }

    /// <summary>
    /// Children in compressed-row form, by counting sort: one pass to count children per node, a
    /// prefix sum, then one pass to place them. Two linear passes and two arrays, with no list per
    /// directory — which on a volume with 240k directories is 240k objects avoided for a structure
    /// that never changes after construction (G4).
    /// </summary>
    private static (int[] Start, int[] Children) InvertParentLinks(int[] parents, bool[] present, int rootNode)
    {
        var count = parents.Length;
        var start = new int[count + 1];

        for (var i = 0; i < count; i++)
        {
            // The root is its own parent, so linking it would make the tree cyclic and the walk
            // below would never terminate.
            if (present[i] && i != rootNode)
            {
                start[parents[i] + 1]++;
            }
        }

        for (var i = 0; i < count; i++)
        {
            start[i + 1] += start[i];
        }

        var children = new int[start[count]];
        var cursor = new int[count];

        for (var i = 0; i < count; i++)
        {
            if (present[i] && i != rootNode)
            {
                var parent = parents[i];
                children[start[parent] + cursor[parent]++] = i;
            }
        }

        return (start, children);
    }

    /// <summary>
    /// Every node reachable from the root, parents before children.
    ///
    /// <para>Iterative, because recursion would overflow the stack on a deep <c>node_modules</c>
    /// tree — exactly the kind of tree this feature exists to show. Reachability is not incidental
    /// either: a table read from a live volume holds records whose parent chain does not reach the
    /// root, and totalling those into anything would attribute bytes to a directory that does not
    /// contain them.</para>
    /// </summary>
    private static int[] DepthFirstOrder(int[] childStart, int[] children, int rootNode)
    {
        var order = new List<int>();
        var stack = new Stack<int>();
        stack.Push(rootNode);

        while (stack.TryPop(out var node))
        {
            order.Add(node);

            for (var i = childStart[node]; i < childStart[node + 1]; i++)
            {
                stack.Push(children[i]);
            }
        }

        return [.. order];
    }

    /// <summary>
    /// Total each subtree by walking the depth-first order backwards, so every node's children are
    /// finished before it is reached. One pass, no recursion and no second traversal.
    ///
    /// <para>An unknown size travels the same way. One record the scan could not size makes every
    /// total above it a lower bound, and a lower bound presented as a measurement is the one thing
    /// a size picture must not do.</para>
    /// </summary>
    private static void RollUp(int[] order, int[] parents, long[] sizes, bool[] sizeUnknown, int rootNode)
    {
        for (var i = order.Length - 1; i >= 0; i--)
        {
            var node = order[i];
            if (node == rootNode)
            {
                continue;
            }

            var parent = parents[node];
            sizes[parent] += sizes[node];
            sizeUnknown[parent] |= sizeUnknown[node];
        }
    }

    /// <summary>
    /// Order each node's children largest first, in place, once. See <see cref="ChildrenOf"/> for
    /// why the order belongs here rather than in each consumer.
    /// </summary>
    private static void SortChildrenBySize(int[] childStart, int[] children, long[] sizes)
    {
        for (var node = 0; node + 1 < childStart.Length; node++)
        {
            var from = childStart[node];
            var length = childStart[node + 1] - from;

            if (length <= 1)
            {
                continue;
            }

            var slice = children.AsSpan(from, length);
            var keys = new long[length];

            for (var i = 0; i < length; i++)
            {
                // Negated rather than sorted and then reversed: Array.Sort is not stable, so a
                // reverse would shuffle equal-sized siblings differently on every scan of the same
                // disk, and the picture would rearrange for no reason the user can see.
                keys[i] = -sizes[slice[i]];
            }

            MemoryExtensions.Sort(keys.AsSpan(), slice);
        }
    }
}
