using System.Runtime.CompilerServices;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Exploring;

/// <summary>
/// The columns a read of the file table fills, one slot a record, which
/// <see cref="ExploreTree.Create"/> is handed once the read ends, and the copy of them a snapshot is
/// drawn from while the read goes on.
///
/// <para>Apart from <see cref="MftExploreReader"/> because two threads meet here: the parse threads
/// write a record's slot, and a snapshot reads every slot beside them. What makes that safe is one
/// rule, kept in the two methods below and nowhere else. <see cref="Describe"/> writes a slot's
/// flags last, with a release store, and <see cref="CopyPresent"/> reads them first, with an acquire
/// load, so a slot a copy sees marked <see cref="ExploreNodeFlags.Present"/> has every other column
/// written. A slot still being described reads as absent, and is drawn by a later snapshot.</para>
/// </summary>
internal sealed class MftExploreColumns
{
    // The token is asked once every 65,536 slots copied, as ExploreTreeCapture asks it.
    private const int CancellationMask = (1 << 16) - 1;

    /// <param name="count">How many slots: one a record, and one a further name of a hard-linked file.</param>
    public MftExploreColumns(int count)
    {
        // A number per record into one pool of names, rather than a string per record. See NamePool.
        // A slot no record fills keeps zero, which is the empty name.
        NameNumbers = new int[count];
        Parents = new int[count];
        Sizes = new long[count];

        // Nine bytes a record more, and kept on purpose. The picture draws space on disk, and a
        // length beside it is what explains a file the cloud holds — 200 GB long and nothing here —
        // to a reader who would otherwise take the gap for a fault in the scan.
        Lengths = new long[count];
        Storage = new FileStorage[count];

        // One byte a record for every flag, set in one store per record. Each record is handed on
        // once, to one thread, and those held back for their extension records one at a time after
        // the parse threads have finished, so no two threads ever write one byte.
        Flags = new ExploreNodeFlags[count];

        // Four bytes per node each, not eight. This route sizes every array to the whole record
        // count before it reads one — 2.4M on an ordinary system volume — so a pair of DateTime
        // arrays here would be 38 MB allocated up front and mostly into slots no record ever fills.
        // See ExploreTimestamp for what the minute of precision buys.
        Created = new ExploreTimestamp[count];
        Modified = new ExploreTimestamp[count];

        // A byte a record, which is the whole cost: the attributes come from the
        // $STANDARD_INFORMATION the dates are already read from.
        Visibility = new FileVisibility[count];
    }

    /// <summary>
    /// Each slot's name, as numbered in the read's <see cref="NamePool"/>. Written for a record
    /// before <see cref="Describe"/> is, so the flags' release store publishes it too.
    /// </summary>
    public int[] NameNumbers { get; private set; }

    public int[] Parents { get; private set; }

    public long[] Sizes { get; private set; }

    public long[] Lengths { get; private set; }

    public FileStorage[] Storage { get; private set; }

    public ExploreNodeFlags[] Flags { get; private set; }

    public ExploreTimestamp[] Created { get; private set; }

    public ExploreTimestamp[] Modified { get; private set; }

    public FileVisibility[] Visibility { get; private set; }

    /// <summary>
    /// Write one slot: a record under the name it was placed by, or one of a file's other names,
    /// which reads its record's size, dates and flags. Each slot is written by one thread.
    ///
    /// <para>The flags go last, released, which is what <see cref="CopyPresent"/> relies on.</para>
    /// </summary>
    public void Describe(int node, in MftRecord record, int parent)
    {
        Parents[node] = parent;
        Sizes[node] = record.Size?.Allocated ?? 0;
        Lengths[node] = record.Size?.Logical ?? 0;
        Storage[node] = record.Storage;
        Created[node] = ExploreTimestamp.FromFileTime(record.CreatedFileTime);
        Modified[node] = ExploreTimestamp.FromFileTime(record.LastWrittenFileTime);
        Visibility[node] = record.Visibility;

        var flags = ExploreNodeFlags.Present
            | (record.IsDirectory ? ExploreNodeFlags.Directory : ExploreNodeFlags.None)
            | (record.IsReparsePoint ? ExploreNodeFlags.Link : ExploreNodeFlags.None)
            | (record.Size is null ? ExploreNodeFlags.SizeUnknown : ExploreNodeFlags.None);

        Volatile.Write(ref FlagsByte(Flags, node), (byte)flags);
    }

    /// <summary>
    /// Make room for <paramref name="count"/> slots, keeping every one written. Only once the read
    /// has ended, because each column is replaced by a longer one.
    /// </summary>
    public void Grow(int count)
    {
        NameNumbers = Resized(NameNumbers, count);
        Parents = Resized(Parents, count);
        Sizes = Resized(Sizes, count);
        Lengths = Resized(Lengths, count);
        Storage = Resized(Storage, count);
        Flags = Resized(Flags, count);
        Created = Resized(Created, count);
        Modified = Resized(Modified, count);
        Visibility = Resized(Visibility, count);
    }

    /// <summary>
    /// The slots described so far, copied into columns of their own while the parse threads go on
    /// writing these. A slot not yet marked <see cref="ExploreNodeFlags.Present"/> is left empty in
    /// the copy, and so is drawn as a record the read has not reached.
    ///
    /// <para>The copy is what a snapshot hands to <see cref="ExploreTree.Create"/>, which totals and
    /// renumbers into what it is given, so these columns are never handed over while the read
    /// runs.</para>
    /// </summary>
    /// <param name="ct">Asked every so many slots, so a snapshot the scan has finished with stops.</param>
    public MftExploreColumns CopyPresent(CancellationToken ct)
    {
        var count = Parents.Length;
        var copy = new MftExploreColumns(count);

        for (var i = 0; i < count; i++)
        {
            if ((i & CancellationMask) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var flags = (ExploreNodeFlags)Volatile.Read(ref FlagsByte(Flags, i));
            if ((flags & ExploreNodeFlags.Present) == 0)
            {
                continue;
            }

            copy.Flags[i] = flags;
            copy.NameNumbers[i] = NameNumbers[i];
            copy.Parents[i] = Parents[i];
            copy.Sizes[i] = Sizes[i];
            copy.Lengths[i] = Lengths[i];
            copy.Storage[i] = Storage[i];
            copy.Created[i] = Created[i];
            copy.Modified[i] = Modified[i];
            copy.Visibility[i] = Visibility[i];
        }

        return copy;
    }

    // Volatile has no overload for an enum, and the flags are one byte each.
    private static ref byte FlagsByte(ExploreNodeFlags[] flags, int node) =>
        ref Unsafe.As<ExploreNodeFlags, byte>(ref flags[node]);

    private static T[] Resized<T>(T[] column, int count)
    {
        Array.Resize(ref column, count);
        return column;
    }
}
