namespace Deguffer.Core.Exploring.Files;

/// <summary>
/// The kind of file every node of one tree holds the most bytes of: a file's own kind, and for a
/// folder the kind with the most bytes anywhere below it. What a map coloured by type paints.
///
/// <para>A tie goes to the kind earlier in <see cref="FileCategories.All"/>, so the same folder is
/// painted the same on every drawing, and a folder holding no bytes in any file is
/// <see cref="FileCategory.Other"/>. The bytes a folder holds of its own count as
/// <see cref="FileCategory.Other"/>, as <see cref="TypeBreakdown"/> counts them, so the first share
/// of the breakdown of a folder holding any bytes is the kind it is painted. A folder holding none
/// is not drawn at all.</para>
///
/// <para>One pass over the tree, kept for the tree's life (G4), and a byte per node: a volume runs to
/// millions of nodes, and keeping every folder's bytes per kind would cost eighty bytes each. The pass
/// holds those totals only for the folders on the way down to the node it is at.</para>
/// </summary>
public sealed class DominantTypes
{
    /// <summary>How many nodes are visited between two looks at the cancellation token, as in <see cref="LargestFiles"/>.</summary>
    private const int CancellationStride = 4096;

    private readonly byte[] _types;

    private DominantTypes(ExploreTree tree, byte[] types)
    {
        Tree = tree;
        _types = types;
    }

    /// <summary>The tree this describes. A node number means nothing in any other.</summary>
    public ExploreTree Tree { get; }

    /// <summary>The kind of file <paramref name="node"/> holds the most bytes of.</summary>
    public FileCategory Of(int node) => (FileCategory)_types[node];

    /// <summary>
    /// The kind a map coloured by type paints <paramref name="node"/> of <paramref name="tree"/>, and
    /// the kind the line under it names: what <paramref name="measured"/> says where it describes this
    /// tree, and otherwise a file's own kind, which its name alone says, and null for a folder, which
    /// is not measured yet. One rule for both, so the words never name a kind the colour does not.
    /// </summary>
    public static FileCategory? KindOf(ExploreTree tree, int node, DominantTypes? measured)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (measured is not null && ReferenceEquals(measured.Tree, tree))
        {
            return measured.Of(node);
        }

        return tree.IsDirectory(node) ? null : FileCategories.Of(tree.NameOf(node));
    }

    /// <summary>Measure every node the root of <paramref name="tree"/> reaches, in one pass.</summary>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled during the pass.</exception>
    public static DominantTypes Measure(ExploreTree tree, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var types = new byte[tree.NodeCount];
        var walk = new Walk(tree);
        var visited = 0;

        walk.Enter(tree.RootNode);

        // Depth first, finishing each folder after everything in it, so its totals are complete when
        // its kind is settled and can then be added to its parent's. Iterative for the reason
        // ExploreTree's own passes are.
        while (walk.Depth >= 0)
        {
            if (++visited % CancellationStride == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (walk.NextChild() is not { } child)
            {
                types[walk.Node] = (byte)Largest(walk.Totals);
                walk.Leave();
                continue;
            }

            if (tree.IsDirectory(child))
            {
                walk.Enter(child);
                continue;
            }

            var category = FileCategories.Of(tree.NameOf(child));

            types[child] = (byte)category;
            walk.Totals[(int)category] += tree.SizeOf(child);
        }

        return new DominantTypes(tree, types);
    }

    /// <summary>The kind with the most bytes, by the rule this type states.</summary>
    private static FileCategory Largest(ReadOnlySpan<long> totals)
    {
        var largest = FileCategory.Other;
        var most = 0L;

        foreach (var category in FileCategories.All)
        {
            if (totals[(int)category] > most)
            {
                most = totals[(int)category];
                largest = category;
            }
        }

        return largest;
    }

    /// <summary>
    /// The folders from the root down to the one being read: which child of each is next, and the
    /// bytes per kind found below each so far. A level's arrays are kept when the walk climbs out of
    /// it and reused when it next goes that deep, so the pass allocates per level reached rather than
    /// per folder (G5).
    /// </summary>
    private sealed class Walk(ExploreTree tree)
    {
        private readonly List<int> _nodes = [];
        private readonly List<int> _next = [];
        private readonly List<long[]> _totals = [];

        public int Depth { get; private set; } = -1;

        /// <summary>The folder being read.</summary>
        public int Node => _nodes[Depth];

        /// <summary>The bytes per kind found below <see cref="Node"/> so far, indexed by <see cref="FileCategory"/>.</summary>
        public long[] Totals => _totals[Depth];

        /// <summary>Start reading <paramref name="folder"/>, counting the bytes it holds of its own as <see cref="FileCategory.Other"/>.</summary>
        public void Enter(int folder)
        {
            Depth++;

            if (Depth == _nodes.Count)
            {
                _nodes.Add(folder);
                _next.Add(0);
                _totals.Add(new long[FileCategories.All.Count]);
            }
            else
            {
                _nodes[Depth] = folder;
                _next[Depth] = 0;
                Array.Clear(_totals[Depth]);
            }

            var own = tree.SizeOf(folder);

            foreach (var child in tree.ChildrenOf(folder))
            {
                own -= tree.SizeOf(child);
            }

            Totals[(int)FileCategory.Other] = own;
        }

        /// <summary>The next child of <see cref="Node"/> to read, or null once every one has been.</summary>
        public int? NextChild()
        {
            var children = tree.ChildrenOf(Node);

            return _next[Depth] < children.Length ? children[_next[Depth]++] : null;
        }

        /// <summary>Finish <see cref="Node"/>, adding what was found below it to its parent's totals.</summary>
        public void Leave()
        {
            if (Depth > 0)
            {
                var (from, to) = (_totals[Depth], _totals[Depth - 1]);

                for (var at = 0; at < from.Length; at++)
                {
                    to[at] += from[at];
                }
            }

            Depth--;
        }
    }
}
