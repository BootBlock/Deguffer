namespace Deguffer.Core.Exploring;

/// <summary>
/// What <see cref="ExploreTree.Create"/> reuses from one tree to the next of one file-table read: its
/// snapshots, drawn from columns the parse threads are still filling out of order (#1015), and then
/// the finished tree, drawn from the same columns once the threads stop (#1083).
///
/// <para>Given this, a tree is always compacted (<see cref="ReachedNodes"/>), whether or not its root
/// reaches every slot, so the totals go into copies of the tree's own, and the tree keeps none of this
/// room. A snapshot needs both, because the columns are never its to keep or to total into. Every
/// array here is sized to the whole table, so made for every tree it would put that much on the large
/// object heap each time: the child starts and child list built before compaction, the top-down
/// order, and the renumbering's two maps.</para>
///
/// <para>The names' sort keys are kept here too, numbered as the read's pool numbers the names. The
/// pool numbers each name once and for good, so a key made for one tree holds for every later one,
/// and each tree makes keys only for the names met since the last. A compacted tree can number its
/// names again (<see cref="NamePool.Keeping"/>), so the read's numbers of the nodes kept are copied
/// aside first, and the keys are found by those.</para>
///
/// <para>One scratch serves one read, whose pool the keys were made from, and one assembling at a
/// time, which <see cref="ScanSnapshots"/> guarantees, and the finished tree is assembled only once
/// the snapshots have stopped. An assembling stopped part way leaves nothing the next one reads before
/// writing it.</para>
/// </summary>
internal sealed class SnapshotScratch
{
    private UInt128[] _nameKeys = [];
    private int _keyed;
    private int _room;

    /// <param name="room">How many nodes a tree can have before <see cref="MakeRoom"/> makes more.</param>
    public SnapshotScratch(int room) => Allocate(room);

    /// <summary>Room for each node's first child, and one more for the end of the last.</summary>
    public int[] Start { get; private set; } = [];

    /// <summary>Room for every node as a child, which is more than there are children.</summary>
    public int[] Children { get; private set; } = [];

    /// <summary>Room for the top-down order of every node.</summary>
    public int[] Order { get; private set; } = [];

    /// <summary>Room for the node each kept node was, in the order they are kept.</summary>
    public int[] Kept { get; private set; } = [];

    /// <summary>Room for each node's number once compacted.</summary>
    public int[] Renumbered { get; private set; } = [];

    /// <summary>
    /// Room for each kept node's name as the read's pool numbers it, copied before the tree's names
    /// may be numbered again. See the remarks on this type.
    /// </summary>
    public int[] ReadNameNumbers { get; private set; } = [];

    /// <summary>
    /// Make room for a tree of <paramref name="nodes"/> nodes, where there is less: made again,
    /// exactly, because only the finished tree, the last this room serves, can have more nodes than
    /// the room it was made with (<see cref="MftSnapshotColumns"/>). Nothing in the room is kept
    /// from one tree to the next but the sort keys, which are not sized by the nodes.
    /// </summary>
    public void MakeRoom(int nodes)
    {
        if (nodes > _room)
        {
            Allocate(nodes);
        }
    }

    private void Allocate(int room)
    {
        _room = room;
        Start = new int[room + 1];
        Children = new int[room];
        Order = new int[room];
        Kept = new int[room];
        Renumbered = new int[room];
        ReadNameNumbers = new int[room];
    }

    /// <summary>
    /// Copy the kept nodes' <paramref name="nameNumbers"/>, numbered as the read's pool numbers the
    /// names, to <see cref="ReadNameNumbers"/>.
    /// </summary>
    public void KeepReadNumbers(int[] nameNumbers) => nameNumbers.CopyTo(ReadNameNumbers, 0);

    /// <summary>
    /// The sort keys for the nodes <paramref name="nameNumbers"/> names in <paramref name="names"/>,
    /// making keys for whatever <paramref name="readNames"/> holds that no tree before this one had.
    /// </summary>
    /// <param name="readNames">
    /// The read's pool as it stood when this tree's names were numbered, before any were numbered
    /// again: the pool <see cref="KeepReadNumbers"/> was given numbers from.
    /// </param>
    public NameSortKeys NameKeys(NamePool.Frozen readNames, int[] nameNumbers, NamePool.Frozen names, CancellationToken ct)
    {
        var count = readNames.Count;

        if (count > _nameKeys.Length)
        {
            // Twice the size, so a pool that grows through the read is copied a handful of times
            // rather than once a snapshot.
            var grown = new UInt128[Math.Max(count, _nameKeys.Length * 2)];
            Array.Copy(_nameKeys, grown, _keyed);
            _nameKeys = grown;
        }

        if (count > _keyed)
        {
            NameSortKeys.Make(_nameKeys, _keyed, readNames, ExploreTreeAssembly.SortWorkers, ct);

            // Only once every key is made, so a snapshot stopped part way leaves the rest to the next.
            _keyed = count;
        }

        return NameSortKeys.Kept(_nameKeys, ReadNameNumbers, nameNumbers, names);
    }
}
