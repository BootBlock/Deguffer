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
///
/// <para>The finished tree reuses <see cref="Scratch"/> too, and it numbers a hard-linked file's
/// further names after every record, so the room is made for those beside the records, which the
/// snapshots alone would not need: made again for the finished tree, it would be the very
/// allocation reusing it avoids (#1083).</para>
/// </summary>
internal sealed class MftSnapshotColumns
{
    /// <summary>
    /// Records for each further name of a hard-linked file the room is made for. The system volume
    /// measured in <see cref="Scanning.AutoTuning"/> held 217,591 such names over 2.78 million
    /// records, one for every thirteen, so one for every eight leaves room for a volume with more. A
    /// volume with more still has the room made again for its finished tree
    /// (<see cref="SnapshotScratch.MakeRoom"/>), so this sets what the read allocates, never what
    /// it draws.
    /// </summary>
    private const int RecordsPerLink = 8;

    /// <param name="count">How many slots the read's columns have: one a record.</param>
    public MftSnapshotColumns(int count)
    {
        Flags = new ExploreNodeFlags[count];
        Parents = new int[count];
        Scratch = new SnapshotScratch(count + (count / RecordsPerLink));
    }

    public ExploreNodeFlags[] Flags { get; }

    public int[] Parents { get; }

    /// <summary>What <see cref="ExploreTree.Create"/> reuses from one tree of the read to the next.</summary>
    public SnapshotScratch Scratch { get; }
}
