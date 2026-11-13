namespace Deguffer.Core.Exploring;

/// <summary>
/// What <see cref="ExploreTree.Create"/> reuses from one tree to the next for a reader that hands it
/// columns it is still writing, so are never the tree's to keep or to total into: the file table's
/// read, whose snapshots are drawn while its parse threads fill the columns out of order (#1015).
///
/// <para>Given this, a tree is always compacted (<see cref="ReachedNodes"/>), whether or not its root
/// reaches every slot, so the totals go into copies of the tree's own. That is also what makes the
/// rest reusable: the child starts and child list built before compaction, and the renumbering's two
/// maps, are each replaced by compaction and so are never kept by the tree. Without this, each of
/// those was an array sized to the whole table, made again for every snapshot on the large object
/// heap.</para>
///
/// <para>The names' sort keys are kept here too, numbered as the read's pool numbers the names. The
/// pool numbers each name once and for good, so a key made for one snapshot holds for every later
/// one, and each snapshot makes keys only for the names met since the last. A compacted tree can
/// number its names again (<see cref="NamePool.Keeping"/>), so the read's numbers of the nodes kept
/// are copied aside first, and the keys are found by those.</para>
///
/// <para>One scratch serves one read, whose pool the keys were made from, and one assembling at a
/// time, which <see cref="ScanSnapshots"/> guarantees. An assembling stopped part way leaves nothing
/// the next one reads before writing it.</para>
/// </summary>
internal sealed class SnapshotScratch
{
    private UInt128[] _nameKeys = [];
    private int _keyed;

    /// <param name="count">How many slots the reader's columns have.</param>
    public SnapshotScratch(int count)
    {
        Start = new int[count + 1];
        Children = new int[count];
        Order = new int[count];
        Kept = new int[count];
        Renumbered = new int[count];
        ReadNameNumbers = new int[count];
    }

    /// <summary>Room for each slot's first child, and one more for the end of the last.</summary>
    public int[] Start { get; }

    /// <summary>Room for every slot as a child, which is more than there are children.</summary>
    public int[] Children { get; }

    /// <summary>Room for the top-down order of every slot.</summary>
    public int[] Order { get; }

    /// <summary>Room for the slot each kept node was, in the order they are kept.</summary>
    public int[] Kept { get; }

    /// <summary>Room for each slot's number once compacted.</summary>
    public int[] Renumbered { get; }

    /// <summary>
    /// Room for each kept node's name as the read's pool numbers it, copied before the tree's names
    /// may be numbered again. See the remarks on this type.
    /// </summary>
    public int[] ReadNameNumbers { get; }

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
