using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// The columns of a walk's tree that never change once a node is written, kept from one snapshot
/// of the walk to the next rather than copied out again for each.
///
/// <para>A walk only adds nodes, and a node's name, parent, creation time and visibility are fixed
/// in the call that adds it. So each snapshot needs those columns only for the nodes added since the
/// one before, and every snapshot can read one set of arrays: each tree reads the slots below its own
/// node count, and a later fill writes only above the count of the fill before it. When the nodes
/// outgrow the arrays, new ones twice the size take over and the trees already made keep the old,
/// which nothing writes again. A full set of columns is about 51 bytes a node. Sharing these leaves a
/// snapshot about 30 bytes a node, for the columns totalled again in each one, and a snapshot whose
/// nodes outgrow the shared arrays allocates the new ones as well (#738).</para>
///
/// <para>The rolled-up columns (sizes, lengths, storage, flags and newest writes) and the child lists
/// change with every node added below them, so each tree has its own. Sharing those would mean taking
/// arrays back from a tree the window may still be drawing, and a tree read from arrays written
/// again names the wrong things.</para>
///
/// <para>Two buffers are scratch, read only while a tree is assembled and never kept by it: each
/// node's name rank among its siblings, and the top-down order the totals are rolled up in.</para>
///
/// <para>Filled by one assembling at a time, which <see cref="ScanSnapshots"/> guarantees. Nothing
/// here is safe for two fills at once.</para>
/// </summary>
internal sealed class SnapshotColumns
{
    private int _filled;

    public int[] NameNumbers { get; private set; } = [];

    public int[] Parents { get; private set; } = [];

    /// <summary>Scratch: see the remarks on this type. Rewritten in part for parents listed more than once.</summary>
    public int[] NameRanks { get; private set; } = [];

    public ExploreTimestamp[] Created { get; private set; } = [];

    public FileVisibility[] Visibility { get; private set; } = [];

    /// <summary>Scratch: room for the top-down order of every node filled so far.</summary>
    public int[] Order { get; private set; } = [];

    /// <summary>
    /// Write the never-changing columns of every node of <paramref name="nodes"/> not yet written, so
    /// each column holds at least as many nodes as it does.
    /// </summary>
    /// <param name="ct">Asked every so many nodes. A fill stopped part way is begun again by the next.</param>
    public void Fill(AppendOnlyChunks<ExploreTreeBuilder.Node>.Prefix nodes, int cancellationMask, CancellationToken ct)
    {
        var count = nodes.Count;

        if (count > NameNumbers.Length)
        {
            Grow(Math.Max(count, NameNumbers.Length * 2));
        }

        for (var i = _filled; i < count; i++)
        {
            if ((i & cancellationMask) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            // By reference, field by field, so that HasChildren, which the builder may still be
            // writing, is never read.
            ref readonly var node = ref nodes[i];

            NameNumbers[i] = node.Name;
            Parents[i] = node.Parent;
            NameRanks[i] = node.NameRank;
            Created[i] = node.Created;
            Visibility[i] = node.Visibility;
        }

        _filled = count;
    }

    /// <summary>
    /// New arrays of <paramref name="capacity"/> slots, holding what the old ones held. The old ones
    /// are left as they are for the trees that read them.
    /// </summary>
    private void Grow(int capacity)
    {
        NameNumbers = Grown(NameNumbers, capacity, _filled);
        Parents = Grown(Parents, capacity, _filled);
        NameRanks = Grown(NameRanks, capacity, _filled);
        Created = Grown(Created, capacity, _filled);
        Visibility = Grown(Visibility, capacity, _filled);

        // Scratch, so nothing in it is worth copying.
        Order = new int[capacity];
    }

    private static T[] Grown<T>(T[] column, int capacity, int filled)
    {
        var grown = new T[capacity];
        Array.Copy(column, grown, filled);

        return grown;
    }
}
