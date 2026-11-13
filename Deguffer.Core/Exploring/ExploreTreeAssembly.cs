using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// The passes <see cref="ExploreTree.Create"/> builds a tree's child lists, order and totals with:
/// invert the parent links, order the nodes top-down, total each subtree, and order each folder's
/// children.
///
/// <para>Apart from <see cref="ExploreTree"/>, which reads what these make, because none of them
/// reads a tree: each is a pass over the arrays a reader hands over, and none keeps anything.</para>
/// </summary>
internal static class ExploreTreeAssembly
{
    /// <summary>
    /// Children in compressed-row form, by counting sort: one pass to count children per node, a
    /// prefix sum, then one pass to place them. Two linear passes and two arrays, with no list per
    /// directory — which on a volume with 240k directories is 240k objects avoided for a structure
    /// that never changes after construction (G4).
    ///
    /// <para>With <paramref name="nameRanks"/>, a child goes where its rank puts it in its parent's
    /// range rather than after the sibling placed before it, so the ranges come out in name order.
    /// Without, each parent's own start is the cursor that places its children, and the starts are
    /// moved back afterwards: one pass rather than a cursor array the size of the tree.</para>
    ///
    /// <para>The nodes of <paramref name="links"/> are numbered after the first
    /// <paramref name="nodes"/> of <paramref name="parents"/>, so each pass runs over the one and then
    /// the other, and each child list holds them in that order.</para>
    ///
    /// <para>With <paramref name="scratch"/>, both arrays are its own, which a tree that is always
    /// compacted never keeps. The child list there is longer than the children, and only the ranges
    /// the starts give are read.</para>
    /// </summary>
    internal static (int[] Start, int[] Children) InvertParentLinks(
        int nodes, int[] parents, ExploreNodeFlags[] flags, MftExploreColumns links, int[]? nameRanks, SnapshotScratch? scratch)
    {
        var count = nodes + links.Count;
        var start = scratch?.Start ?? new int[count + 1];

        // What the tree before this one counted is still there.
        Array.Clear(start, 0, count + 1);

        CountChildren(nodes, parents, flags, first: 0, start);
        CountChildren(links.Count, links.Parents, links.Flags, first: nodes, start);

        for (var i = 0; i < count; i++)
        {
            start[i + 1] += start[i];
        }

        var children = scratch?.Children ?? new int[start[count]];

        if (nameRanks is not null)
        {
            for (var i = 0; i < nodes; i++)
            {
                if (IsLinked(parents, flags, i, i))
                {
                    children[start[parents[i]] + nameRanks[i]] = i;
                }
            }

            return (start, children);
        }

        PlaceChildren(nodes, parents, flags, first: 0, start, children);
        PlaceChildren(links.Count, links.Parents, links.Flags, first: nodes, start, children);

        // Each start has advanced to the end of its range, which is where the next node's range
        // starts, so every start now sits one place early. The last never moved, because no node
        // has a parent past the end.
        for (var node = count; node > 0; node--)
        {
            start[node] = start[node - 1];
        }

        start[0] = 0;

        return (start, children);
    }

    /// <summary>
    /// Whether the node in <paramref name="slot"/> of these columns, numbered <paramref name="node"/>,
    /// is some node's child.
    ///
    /// <para>A node that is its own parent is not linked to anything, because linking it would make
    /// the tree cyclic and the walk below would never terminate. Every reader marks its scan root
    /// that way, so this is how the root stays out of its own child list.</para>
    ///
    /// <para>Stated as the shape rather than as "is it the root", because a scan rooted at a folder
    /// leaves the volume's own root somewhere above it — still present, still its own parent, and
    /// still unreachable from the scan's root. Testing the node number alone would link that one
    /// under itself.</para>
    /// </summary>
    private static bool IsLinked(int[] parents, ExploreNodeFlags[] flags, int slot, int node) =>
        (flags[slot] & ExploreNodeFlags.Present) != 0 && parents[slot] != node;

    /// <summary>
    /// Count each node's children among the first <paramref name="nodes"/> slots of columns whose
    /// first slot is node <paramref name="first"/>, each one place along in <paramref name="start"/>,
    /// ready for the sum that makes them starts.
    /// </summary>
    private static void CountChildren(int nodes, int[] parents, ExploreNodeFlags[] flags, int first, int[] start)
    {
        for (var i = 0; i < nodes; i++)
        {
            if (IsLinked(parents, flags, i, first + i))
            {
                start[parents[i] + 1]++;
            }
        }
    }

    /// <summary>
    /// Place each of the first <paramref name="nodes"/> nodes of columns whose first slot is node
    /// <paramref name="first"/> after its parent's children placed so far, moving that parent's start
    /// along.
    /// </summary>
    private static void PlaceChildren(int nodes, int[] parents, ExploreNodeFlags[] flags, int first, int[] start, int[] children)
    {
        for (var i = 0; i < nodes; i++)
        {
            if (IsLinked(parents, flags, i, first + i))
            {
                children[start[parents[i]]++] = first + i;
            }
        }
    }

    /// <summary>
    /// Every node reachable from the root, parents before children.
    ///
    /// <para>Iterative, because recursion would overflow the stack on a deep <c>node_modules</c>
    /// tree — exactly the kind of tree this feature exists to show. Reachability is not incidental
    /// either: a table read from a live volume holds records whose parent chain does not reach the
    /// root, and totalling those into anything would attribute bytes to a directory that does not
    /// contain them.</para>
    ///
    /// <para>Breadth first, so that the order is its own queue: one array sized to the tree, with no
    /// stack beside it and no list doubling its way through the large object heap. Each node has one
    /// parent, so none is reached twice and the array cannot overflow. Nothing needs depth first:
    /// <see cref="RollUp"/> asks only that a parent comes before its children, and
    /// <see cref="ReachedNodes"/> only which nodes are here.</para>
    /// </summary>
    /// <param name="buffer">Room for every node, or null for an array of its own.</param>
    internal static Span<int> TopDownOrder(int[] childStart, int[] children, int rootNode, int[]? buffer)
    {
        var order = buffer ?? new int[childStart.Length - 1];
        order[0] = rootNode;
        var reached = 1;

        for (var next = 0; next < reached; next++)
        {
            var node = order[next];

            for (var i = childStart[node]; i < childStart[node + 1]; i++)
            {
                order[reached++] = children[i];
            }
        }

        return order.AsSpan(0, reached);
    }

    /// <summary>
    /// Total each subtree by walking the top-down order backwards, so every node's children are
    /// finished before it is reached. One pass, no recursion and no second traversal.
    ///
    /// <para>An unknown size travels the same way. One record the scan could not size makes every
    /// total above it a lower bound, and a lower bound presented as a measurement is the one thing
    /// a size picture must not do.</para>
    ///
    /// <para>So does the last-written time, and it rides this pass rather than getting one of its
    /// own precisely because the traversal is the cost. Each is combined by its own operation — sizes
    /// and lengths add, unknowns and storage spread, times take the later — and the same single
    /// ordering serves them all. See <see cref="ExploreTree.ModifiedOf"/> for why a directory has to answer for its
    /// contents rather than for itself, and note that a creation time is deliberately absent from
    /// this: it is the one of the three that means something on its own.</para>
    /// </summary>
    internal static void RollUp(
        ReadOnlySpan<int> order,
        int[] parents,
        long[] sizes,
        long[] lengths,
        FileStorage[] storage,
        ExploreNodeFlags[] flags,
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
            lengths[parent] += lengths[node];
            storage[parent] |= storage[node];
            flags[parent] |= flags[node] & ExploreNodeFlags.SizeUnknown;

            // Unknown is zero, so it loses to every real date without a case of its own — which is
            // the answer that is wanted. A directory holding one file it could not date and one it
            // could is dated by the one it could, not reduced to "unknown" by the other.
            modified[parent] = ExploreTimestamp.Newer(modified[parent], modified[node]);
        }
    }

    /// <summary>
    /// How many threads order the children of a tree at once. Measured on a real system volume of 2.6
    /// million nodes, 282 thousand of them folders (#553): one thread took about 345 ms, two about 145
    /// and four about 95, the name keys made included. Comparing names in place of the keys took 830,
    /// 340 and 245, two and a half times the 95 ms a synthetic tree of that size had recorded, because
    /// its siblings rarely shared a size. On that synthetic tree thirty-two threads took no less than
    /// four. Threads past four would only be taken from a scan of another volume, which the duplicate
    /// search runs beside this one.
    /// </summary>
    internal static readonly int SortWorkers = Math.Min(Environment.ProcessorCount, 4);

    /// <summary>
    /// Nodes handed to a sorting thread at a time, files among them, so a share holds a few hundred
    /// folders on an ordinary volume: small enough to spread folders of very different sizes.
    /// </summary>
    private const int NodesPerShare = 4096;

    /// <summary>
    /// Order each node's children by <paramref name="childOrder"/>, in place, once. See
    /// <see cref="ExploreTree.ChildrenOf"/> for why the order belongs here rather than in each consumer. Name
    /// order needs no sort where the reader ranked the names: see <see cref="ExploreTree.Create"/>.
    ///
    /// <para>Each node here is one the root reaches (<see cref="ReachedNodes"/>), so a scan of one
    /// folder sorts that folder's tree and not the volume around it. Each folder's children are
    /// ordered apart from every other folder's, so the folders are shared among
    /// <see cref="SortWorkers"/> threads. Names are put in order by <see cref="NameSortKeys"/>, which
    /// says why they are not compared as they are, and which are made for each tree or kept from one
    /// snapshot to the next as <see cref="ExploreTree.Create"/> decides.</para>
    ///
    /// <para>Each worker is one iteration, which takes shares of <see cref="NodesPerShare"/> nodes in
    /// turn until none are left, with buffers made for it here, before any worker starts. A
    /// <see cref="Parallel"/> loop over the shares would make a set of buffers, each sized to the
    /// largest folder, for every task it starts and on whichever thread runs that task, and how many it
    /// starts and where depends on how busy the thread pool is, so a sort's allocations would change
    /// from one run to the next.</para>
    /// </summary>
    internal static void SortChildren(
        int[] childStart,
        int[] children,
        ExploreChildOrder childOrder,
        long[] sizes,
        NameSortKeys nameKeys,
        CancellationToken ct)
    {
        var bySize = childOrder is ExploreChildOrder.BySize;

        // One delegate for the whole tree rather than one per directory. A volume has hundreds of
        // thousands of directories (G5).
        Func<int, long> sizeOf = node => sizes[node];

        // One pair of key buffers for each worker, sized to the largest folder, rather than one per
        // folder.
        var most = 0;
        var nodeCount = childStart.Length - 1;

        for (var node = 0; node < nodeCount; node++)
        {
            most = Math.Max(most, childStart[node + 1] - childStart[node]);
        }

        var shares = (nodeCount + NodesPerShare - 1) / NodesPerShare;
        var buffers = new SortBuffers[Math.Min(SortWorkers, shares)];

        for (var worker = 0; worker < buffers.Length; worker++)
        {
            buffers[worker] = new SortBuffers(most, nameKeys);
        }

        var taken = -1;

        Parallel.For(
            0,
            buffers.Length,
            new ParallelOptions { MaxDegreeOfParallelism = SortWorkers, CancellationToken = ct },
            worker =>
            {
                var own = buffers[worker];

                // The token is asked between shares, as the loop over the shares asked it, because a
                // worker here is one iteration that the loop never comes back to.
                for (int share; !ct.IsCancellationRequested && (share = Interlocked.Increment(ref taken)) < shares;)
                {
                    var end = Math.Min((share + 1) * NodesPerShare, nodeCount);

                    for (var node = share * NodesPerShare; node < end; node++)
                    {
                        var from = childStart[node];
                        var length = childStart[node + 1] - from;

                        if (length <= 1)
                        {
                            continue;
                        }

                        var siblings = children.AsSpan(from, length);

                        if (bySize)
                        {
                            ExploreTree.OrderBySize(siblings, own.SizeKeys, sizeOf, own.ByName);
                        }
                        else
                        {
                            own.ByName(siblings);
                        }
                    }
                }
            });

        // A worker that saw the token stopped taking shares and returned, leaving folders unsorted.
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// What one sorting worker orders its folders with: a key buffer for sizes, one for names, and the
    /// name order that writes to the second, made once for the worker rather than once per folder.
    /// </summary>
    private sealed class SortBuffers
    {
        public SortBuffers(int most, NameSortKeys nameKeys)
        {
            var nameKeyBuffer = new UInt128[most];

            SizeKeys = new long[most];
            ByName = siblings => nameKeys.Order(siblings, nameKeyBuffer);
        }

        public long[] SizeKeys { get; }

        public Action<Span<int>> ByName { get; }
    }
}
