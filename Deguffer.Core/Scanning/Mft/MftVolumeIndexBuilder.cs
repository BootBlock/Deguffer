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
    /// Returns false if any region of the table could not be read. That is deliberately strict: a
    /// partial index still answers every query, and answers some of them short — the caller is told
    /// a cache holds 200 MB when it holds 4 GB, with nothing to indicate the difference. Refusing
    /// costs a slow scan; accepting costs a wrong number in a tool whose numbers decide deletions.
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

                    if (MftRecordParser.TryParse(slice, source.BytesPerSector, out var record)
                        && record.ParentRecordNumber < tree.Count)
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
