namespace Deguffer.Core.Exploring;

/// <summary>
/// What a file-table read's snapshots are drawn from beside the columns the parse threads are
/// writing, made at the first snapshot and reused by every later one (#1015).
///
/// <para>Only the flags and parents are copied (<see cref="MftExploreColumns.CopyPresent"/>), because
/// they are the two columns read at slots a snapshot has not judged present: the inversion of the
/// parent links reads every slot, and the snapshot's rooting writes both at the volume's root and
/// the scan's. Every other column is read where the read writes it, and only at a slot the copy saw
/// present, whose columns will not change again. A copy of all nine would be 35 bytes a record on
/// the large object heap at every snapshot, against five for these two.</para>
///
/// <para>One assembling at a time, which <see cref="ScanSnapshots"/> guarantees, and each copy writes
/// every slot, so nothing an earlier snapshot left is read.</para>
/// </summary>
internal sealed class MftSnapshotColumns
{
    /// <param name="count">How many slots the read's columns have: one a record.</param>
    public MftSnapshotColumns(int count)
    {
        Flags = new ExploreNodeFlags[count];
        Parents = new int[count];
        Order = new int[count];
        Scratch = new SnapshotScratch(count);
    }

    public ExploreNodeFlags[] Flags { get; }

    public int[] Parents { get; }

    /// <summary>Room for the top-down order of every slot.</summary>
    public int[] Order { get; }

    /// <summary>What <see cref="ExploreTree.Create"/> reuses from one snapshot to the next.</summary>
    public SnapshotScratch Scratch { get; }
}
