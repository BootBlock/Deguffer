namespace Deguffer.Core.Exploring;

/// <summary>
/// A scanned tree of directories and files, as parallel arrays indexed by node.
///
/// <para>Parallel arrays for the reason <see cref="Scanning.Mft.MftVolumeTree"/> uses them: a real
/// volume runs to millions of entries, and one object per node would cost more in object headers
/// than every field here put together.</para>
///
/// <para>This holds no knowledge of drawing, of tiers, or of what may be deleted. It answers "what
/// is under this node, how big is it, and when was it touched", and nothing else — which is what
/// lets one tree serve a treemap, a sunburst and a list without any of them being privileged.</para>
/// </summary>
public sealed class ExploreTree
{
    private readonly string[] _names;
    private readonly int[] _parents;
    private readonly long[] _sizes;
    private readonly bool[] _isDirectory;
    private readonly bool[] _isLink;
    private readonly bool[] _sizeUnknown;
    private readonly ExploreTimestamp[] _created;
    private readonly ExploreTimestamp[] _modified;
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
        ExploreTimestamp[] created,
        ExploreTimestamp[] modified,
        int[] childStart,
        int[] children,
        ExploreChildOrder childOrder)
    {
        RootPath = rootPath;
        RootNode = rootNode;
        ChildOrder = childOrder;
        _names = names;
        _parents = parents;
        _sizes = sizes;
        _isDirectory = isDirectory;
        _isLink = isLink;
        _sizeUnknown = sizeUnknown;
        _created = created;
        _modified = modified;
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

    /// <summary>
    /// The order <see cref="ChildrenOf"/> returns siblings in.
    ///
    /// <para>Exposed because it is a precondition rather than a detail. Squarification is defined
    /// only over a decreasing sequence, so a consumer holding a tree ordered any other way has to
    /// draw something else — and a view inferring that from whether a scan was running would be
    /// inferring the one thing that decides whether its picture means anything.</para>
    /// </summary>
    public ExploreChildOrder ChildOrder { get; }

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

    /// <summary>
    /// When this node was made, as the filesystem recorded it, or
    /// <see cref="ExploreTimestamp.Unknown"/> where the scan could not establish it.
    ///
    /// <para>The node's own date, never rolled up. A directory's creation time is the day it was
    /// made and stays that whatever happens inside it, which is exactly the fact worth having: it
    /// tells the age of an installation apart from the age of its contents.</para>
    /// </summary>
    public ExploreTimestamp CreatedOf(int node) => _created[node];

    /// <summary>
    /// The newest write at or below this node, or <see cref="ExploreTimestamp.Unknown"/> where
    /// nothing under it carried a date.
    ///
    /// <para><b>Rolled up, and that is the whole value of it.</b> A file answers for itself. A
    /// directory answers for everything inside it, because NTFS moves a directory's own timestamp
    /// when an entry is added, removed or renamed and leaves it alone when an entry's contents
    /// change — so the directory alone dates the last time its layout altered, not the last time
    /// anything in it was written. <see cref="Providers.DirectoryAge"/> reaches the same conclusion
    /// for a single location one level down; this is that rule applied to a whole volume, which is
    /// affordable here because the pass has already been made.</para>
    ///
    /// <para>This is what §8's first open question asked for. "Is this toolchain idle" is answered
    /// by the newest write anywhere beneath it and by nothing shallower — an Android SDK whose top
    /// directory was touched by an installer last week is idle if nothing inside it has been written
    /// in two years. Last-access times would answer it more directly and NTFS stops maintaining
    /// them by default, which is why the question was open.</para>
    /// </summary>
    public ExploreTimestamp ModifiedOf(int node) => _modified[node];

    public int ParentOf(int node) => _parents[node];

    /// <summary>
    /// This node's children, in <see cref="ChildOrder"/>. Fixed at build time because every
    /// consumer wants an order — a treemap needs descending size for its row packing, a list shows
    /// it directly, and a sunburst draws its arcs in it — so sorting once beats sorting per repaint
    /// (G4/G5).
    /// </summary>
    public ReadOnlySpan<int> ChildrenOf(int node) =>
        _children.AsSpan(_childStart[node], _childStart[node + 1] - _childStart[node]);

    /// <summary>
    /// The full path of <paramref name="node"/>, rebuilt by walking to the root.
    ///
    /// <para>Rebuilt rather than stored: a path per node would cost more than every other array
    /// here combined, and the UI asks for one only when the user points at something.</para>
    ///
    /// <para>Throws for a node whose parent chain does not reach the root. Such nodes exist: the
    /// file-table route sizes its arrays to the whole record count and most of those slots are
    /// never filled, so their parent is a default zero and the walk would never terminate. Every
    /// node reachable through <see cref="ChildrenOf"/> from the root is safe by construction, and
    /// that is the only way anything gets one to pass here.</para>
    ///
    /// <para>Throwing rather than answering, because the caller wants this for a label today and to
    /// open a folder tomorrow. A guessed path is a wrong path, and the one thing worse than no
    /// answer is a plausible one.</para>
    /// </summary>
    public string PathOf(int node)
    {
        if (node == RootNode)
        {
            return RootPath;
        }

        var components = new List<string>();
        var current = node;

        // Bounded by the node count rather than by a chosen depth. A path cannot have more
        // components than the tree has nodes, so this rejects nothing legitimate however deep a
        // real tree turns out to be — and a chain longer than that has a cycle in it, which is the
        // only thing this bound is here to end.
        for (var depth = 0; depth < NodeCount; depth++)
        {
            components.Add(_names[current]);

            var parent = _parents[current];

            // Only the root is its own parent; anything else claiming to be would loop forever.
            if (parent == current)
            {
                break;
            }

            current = parent;

            if (current == RootNode)
            {
                components.Reverse();
                return Path.Combine([RootPath, .. components]);
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(node), node, "This node is not reachable from the root, so it has no path.");
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
    ///
    /// <paramref name="childOrder"/> settles the last of the three. See
    /// <see cref="ExploreChildOrder"/> for why a tree still being filled in wants a different one
    /// from a finished tree.
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
        ExploreTimestamp[] created,
        ExploreTimestamp[] modified,
        bool[] present,
        ExploreChildOrder childOrder)
    {
        var (childStart, children) = InvertParentLinks(parents, present, rootNode);
        var order = DepthFirstOrder(childStart, children, rootNode);

        RollUp(order, parents, sizes, sizeUnknown, modified, rootNode);
        SortChildren(childStart, children, sizes, names, childOrder);

        return new ExploreTree(
            rootPath, rootNode, names, parents, sizes, isDirectory, isLink, sizeUnknown,
            created, modified, childStart, children, childOrder);
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
    ///
    /// <para>So does the last-written time, and it rides this pass rather than getting one of its
    /// own precisely because the traversal is the cost. The three are combined by different
    /// operations — sizes add, unknowns spread, times take the later — and the same single ordering
    /// serves all three. See <see cref="ModifiedOf"/> for why a directory has to answer for its
    /// contents rather than for itself, and note that a creation time is deliberately absent from
    /// this: it is the one of the three that means something on its own.</para>
    /// </summary>
    private static void RollUp(
        int[] order,
        int[] parents,
        long[] sizes,
        bool[] sizeUnknown,
        ExploreTimestamp[] modified,
        int rootNode)
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

            // Unknown is zero, so it loses to every real date without a case of its own — which is
            // the answer that is wanted. A directory holding one file it could not date and one it
            // could is dated by the one it could, not reduced to "unknown" by the other.
            modified[parent] = ExploreTimestamp.Newer(modified[parent], modified[node]);
        }
    }

    /// <summary>
    /// Order each node's children, in place, once. See <see cref="ChildrenOf"/> for why the order
    /// belongs here rather than in each consumer, and <see cref="ExploreChildOrder"/> for why there
    /// is more than one of them.
    /// </summary>
    private static void SortChildren(
        int[] childStart, int[] children, long[] sizes, string[] names, ExploreChildOrder order)
    {
        // One delegate for the whole tree rather than one per directory. A volume has hundreds of
        // thousands of directories, and this runs again on every snapshot of a scan in progress
        // (G5).
        var byName = ByName(names);

        for (var node = 0; node + 1 < childStart.Length; node++)
        {
            var from = childStart[node];
            var length = childStart[node + 1] - from;

            if (length <= 1)
            {
                continue;
            }

            var slice = children.AsSpan(from, length);

            if (order == ExploreChildOrder.ByName)
            {
                MemoryExtensions.Sort(slice, byName);
                continue;
            }

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

    /// <summary>
    /// Compare two siblings by name, ordinally and without regard to case, and break a tie by node
    /// number.
    ///
    /// <para>Ordinal so that the same disk draws the same picture on every machine. Without regard
    /// to case because the alternative sorts every capitalised name ahead of every lower-case one,
    /// which reads as no order at all to somebody looking at the list.</para>
    ///
    /// <para>The tie-break is what makes this a total order rather than one whose equal cases fall
    /// to whatever the sort does with them. Two siblings can compare equal — a directory with case
    /// sensitivity enabled holds <c>Cache</c> and <c>cache</c> at once — and the size path three
    /// lines below says why an unspecified order among equals is not good enough here.</para>
    /// </summary>
    private static Comparison<int> ByName(string[] names) =>
        (left, right) =>
        {
            var byName = string.Compare(names[left], names[right], StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : left.CompareTo(right);
        };
}
