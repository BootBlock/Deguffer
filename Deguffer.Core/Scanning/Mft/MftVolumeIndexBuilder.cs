using System.Buffers;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Reads every record of a table and assembles the arrays <see cref="MftVolumeIndex"/> answers
/// from. Separate from the index itself because building and querying are different jobs with
/// different costs: this runs once per volume and touches millions of records; the index runs per
/// question and touches a handful.
/// </summary>
public static class MftVolumeIndexBuilder
{
    private const int RecordsPerBatch = 1024;

    /// <summary>
    /// Build the index for <paramref name="source"/>.
    ///
    /// Returns false if any region of the table could not be read, or if any record in it could not
    /// be. That is deliberately strict: a partial index still answers every query, and answers some
    /// of them short — the caller is told a cache holds 200 MB when it holds 4 GB, with nothing to
    /// indicate the difference. Refusing costs a slow scan; accepting costs a wrong number in a tool
    /// whose numbers decide deletions.
    ///
    /// Records a table is simply full of — free ones, and the extension records that hold the
    /// attributes of files whose own record ran out of room — are skipped and cost nothing. The
    /// distinction between those and a record that could not be read is <see cref="MftParseOutcome"/>'s
    /// whole reason to exist.
    /// </summary>
    public static bool TryBuild(IMftSource source, out MftVolumeIndex index, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        index = default!;

        var count = (int)Math.Min(source.RecordCount, int.MaxValue);
        var tree = new MftVolumeTree(count);

        if (!ReadAllRecords(source, tree, ct))
        {
            return false;
        }

        index = new MftVolumeIndex(tree, BuildChildLinks(tree));
        return true;
    }

    private static bool ReadAllRecords(IMftSource source, MftVolumeTree tree, CancellationToken ct)
    {
        var batchBytes = RecordsPerBatch * source.BytesPerRecord;
        var buffer = ArrayPool<byte>.Shared.Rent(batchBytes);

        try
        {
            long next = 0;

            while (next < tree.Count)
            {
                ct.ThrowIfCancellationRequested();

                var read = source.ReadBatch(next, buffer.AsSpan(0, batchBytes));
                if (read <= 0)
                {
                    // Skipping ahead would leave a hole in the tree that no later check can see:
                    // the files in the missed range simply never get added, and every directory
                    // above them totals short. Abandon the index instead.
                    return false;
                }

                for (var i = 0; i < read; i++)
                {
                    var slice = buffer.AsSpan(i * source.BytesPerRecord, source.BytesPerRecord);
                    var outcome = MftRecordParser.Parse(slice, source.BytesPerSector, out var record);

                    if (outcome == MftParseOutcome.NotAnEntry)
                    {
                        continue;
                    }

                    // A record in use that this reader cannot place is the same loss as a region it
                    // could not read: a file exists, the tree cannot hold it, and every directory
                    // above it would total short with nothing to show for it.
                    //
                    // The whole volume goes with it, and on a live read that can be one record
                    // caught mid-write — the condition the update sequence array exists to detect,
                    // and one a second read of that record alone would very likely settle. Retrying
                    // is the improvement this wants; refusing is the answer that is never wrong.
                    // …except inside the reserved range, where this shape is not damage but the
                    // format. NTFS marks records 12 to 15 in use and gives them no name, so the
                    // refusal below fired on record 12 of every NTFS volume and took the index with
                    // it — measured on a real volume: those four records, and no others in three
                    // million. §5.5's fast path could therefore never engage on a real machine, and
                    // an elevated run walked every path exactly as an unelevated one did.
                    //
                    // Skipping them loses nothing. None of the sixteen hangs off a user-visible
                    // directory, so no subtree Deguffer totals is short by a byte.
                    if (outcome == MftParseOutcome.Unreadable && next + i < MftRecord.ReservedRecordCount)
                    {
                        continue;
                    }

                    if (outcome == MftParseOutcome.Unreadable)
                    {
                        return false;
                    }

                    // A parent outside the table is a different thing entirely, and an ordinary one
                    // on a live volume: a directory removed mid-read, or a table that grew after its
                    // size was measured. Such a record is unreachable from the root, so it cannot be
                    // inside anything this index will be asked to total, and dropping it costs
                    // nothing.
                    if (record.ParentRecordNumber < tree.Count)
                    {
                        tree.Set(next + i, record);
                    }
                }

                next += read;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Invert the parent links by counting sort: one pass to count children per directory, a prefix
    /// sum, then one pass to place them. Two linear passes and two arrays, no per-node allocation —
    /// a dictionary of lists would allocate one list per directory, which on a volume with 100k
    /// directories is 100k objects for a structure that never changes after construction.
    /// </summary>
    private static MftChildLinks BuildChildLinks(MftVolumeTree tree)
    {
        var start = new int[tree.Count + 1];

        for (var i = 0; i < tree.Count; i++)
        {
            if (tree.IsLinkable(i))
            {
                start[tree.Parent[i] + 1]++;
            }
        }

        for (var i = 0; i < tree.Count; i++)
        {
            start[i + 1] += start[i];
        }

        var children = new uint[start[tree.Count]];
        var cursor = new int[tree.Count];

        for (var i = 0; i < tree.Count; i++)
        {
            if (tree.IsLinkable(i))
            {
                var parent = tree.Parent[i];
                children[start[parent] + cursor[parent]++] = (uint)i;
            }
        }

        return new MftChildLinks(start, children);
    }
}
