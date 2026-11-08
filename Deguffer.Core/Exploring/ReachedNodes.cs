namespace Deguffer.Core.Exploring;

/// <summary>
/// The nodes a tree's root reaches, numbered again from zero with nothing between them, for a reader
/// that numbered more nodes than the tree it builds holds, or numbered them in two sets of columns.
///
/// <para>The file table's reader is that reader. It numbers a node by its record, so it sizes every
/// column to the whole table before it reads one, and keeps a slot for every free record and for
/// every record outside the folder a scan is rooted at. A scan of one folder would otherwise keep
/// every record on the volume for as long as its tree is shown, to draw the few thousand under the
/// folder.</para>
///
/// <para><b>Renumbered in the order the reader numbered them</b>, so a node that came before another
/// still does. That keeps every order drawn from node numbers as it was: siblings whose names differ
/// only in case are told apart by number (<see cref="ExploreTree.CompareByName(int, int)"/>). It also
/// makes this nothing at all for a reader whose every node is reached, which the walk's always is,
/// so a walked node keeps its number from one snapshot of a scan to the next.</para>
///
/// <para><b>From two sets of columns, in one copy.</b> The file table's reader numbers a hard-linked
/// file's further names after every record, in columns of their own, because the record columns
/// are sized to the whole volume and growing them to make room would copy each of them once more.
/// A node numbered past the record columns is read from the columns after them.</para>
/// </summary>
internal sealed class ReachedNodes
{
    private const int NotReached = -1;

    // The number each kept node had, in the order they are kept, and the reverse.
    private readonly int[] _kept;
    private readonly int[] _renumbered;

    // How many slots the first columns have, and how many of the kept nodes are among them. Every
    // kept node from there on is in the columns after them, because the nodes are kept in order.
    private readonly int _first;
    private readonly int _keptFromFirst;

    /// <param name="order">Every node the root reaches, once each, in any order.</param>
    /// <param name="first">How many nodes the reader numbered in its first columns.</param>
    /// <param name="after">How many it numbered after those, in columns of their own.</param>
    public ReachedNodes(ReadOnlySpan<int> order, int first, int after)
    {
        var count = first + after;
        _first = first;
        _renumbered = new int[count];
        Array.Fill(_renumbered, NotReached);

        foreach (var node in order)
        {
            _renumbered[node] = 0;

            if (node < first)
            {
                _keptFromFirst++;
            }
        }

        _kept = new int[order.Length];
        var next = 0;

        for (var node = 0; node < count; node++)
        {
            if (_renumbered[node] != NotReached)
            {
                _kept[next] = node;
                _renumbered[node] = next++;
            }
        }
    }

    /// <summary>The new number of a node the root reaches.</summary>
    public int this[int node] => _renumbered[node];

    /// <summary>
    /// One column holding only the kept nodes, at their new numbers, copied from
    /// <paramref name="column"/> and the column <paramref name="after"/> it.
    /// </summary>
    public T[] Keep<T>(T[] column, T[] after)
    {
        var kept = new T[_kept.Length];

        for (var node = 0; node < _keptFromFirst; node++)
        {
            kept[node] = column[_kept[node]];
        }

        for (var node = _keptFromFirst; node < kept.Length; node++)
        {
            kept[node] = after[_kept[node] - _first];
        }

        return kept;
    }

    /// <summary>
    /// <see cref="Keep{T}"/> for the parent links, each renumbered too. A node the root reaches is
    /// reached through its parent, so every parent kept is one of the kept nodes.
    /// </summary>
    public int[] Parents(int[] parents, int[] after)
    {
        var kept = new int[_kept.Length];

        for (var node = 0; node < _keptFromFirst; node++)
        {
            kept[node] = _renumbered[parents[_kept[node]]];
        }

        for (var node = _keptFromFirst; node < kept.Length; node++)
        {
            kept[node] = _renumbered[after[_kept[node] - _first]];
        }

        return kept;
    }

    /// <summary>
    /// The child links of the kept nodes, renumbered, with each list in the order it had. Every
    /// child of a node the root reaches is reached through it, so no list loses an entry.
    /// </summary>
    public (int[] Start, int[] Children) Links(int[] childStart, int[] children)
    {
        var start = new int[_kept.Length + 1];

        for (var node = 0; node < _kept.Length; node++)
        {
            var was = _kept[node];
            start[node + 1] = start[node] + (childStart[was + 1] - childStart[was]);
        }

        var kept = new int[start[_kept.Length]];

        for (var node = 0; node < _kept.Length; node++)
        {
            var from = childStart[_kept[node]];

            for (var i = 0; i < start[node + 1] - start[node]; i++)
            {
                kept[start[node] + i] = _renumbered[children[from + i]];
            }
        }

        return (start, kept);
    }

    /// <summary>Renumber every node of <paramref name="nodes"/>, each of which the root reaches, in place.</summary>
    public void Renumber(Span<int> nodes)
    {
        foreach (ref var node in nodes)
        {
            node = _renumbered[node];
        }
    }

    /// <summary>The kept nodes of <paramref name="nodes"/>, renumbered, and none of the others.</summary>
    public int[] KeepOnly(IEnumerable<int> nodes) =>
        [.. nodes.Where(node => _renumbered[node] != NotReached).Select(node => _renumbered[node])];
}
