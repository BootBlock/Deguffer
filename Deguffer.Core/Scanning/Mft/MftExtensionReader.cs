using System.Buffers;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The second pass over a table: reads what the first pass's <see cref="MftDeferredRecord"/>s were
/// sent elsewhere for, and folds it in.
///
/// <para>A second pass rather than a read in the middle of the first, so the drive is not sent
/// back and forth across the table. Every extension record wanted is collected first and read in
/// record-number order, so this pass also moves forward through the table, one batch serving every
/// wanted record that falls inside it.</para>
///
/// <para>Bytes only, like <see cref="MftRecordStream"/>. What a record that could not be completed
/// means is for the caller to decide, and each <see cref="MftDeferredRecord"/> already says which
/// it is.</para>
/// </summary>
internal static class MftExtensionReader
{
    /// <summary>
    /// Complete every record in <paramref name="deferred"/>. Returns false where the lists read
    /// here want more extension records than <paramref name="budget"/> allows, which a table NTFS
    /// wrote never does; the records past that point are then lost whole rather than followed.
    /// </summary>
    /// <param name="budget">
    /// How many more extension records the table can hold for these records to want, after the
    /// first pass's own.
    /// </param>
    /// <param name="batch">
    /// A buffer of whole records to read into, lent by the caller so the second pass reuses the
    /// first pass's pooled buffer rather than renting another.
    /// </param>
    public static bool TryResolve(
        IMftSource source,
        IReadOnlyList<MftDeferredRecord> deferred,
        long budget,
        Span<byte> batch,
        CancellationToken ct)
    {
        foreach (var record in deferred)
        {
            ct.ThrowIfCancellationRequested();

            if (record.PendingList is not { } list)
            {
                continue;
            }

            if (budget < 0)
            {
                record.Fail(MftAttributeKinds.Name | MftAttributeKinds.DataStart | MftAttributeKinds.ReparsePoint);
                continue;
            }

            record.Follow(TryReadList(source, list.Runs, list.Length));
            budget -= record.Segments.Count;
        }

        ReadExtensionRecords(source, deferred, batch, ct);
        return budget >= 0;
    }

    /// <summary>
    /// The entries of a list kept outside the table, or null where any part of it cannot be read.
    /// Only the clusters holding the list's bytes are read: the allocation can run past them, and a
    /// sparse run is a hole where entries should be.
    /// </summary>
    private static IReadOnlyList<MftAttributeListEntry>? TryReadList(
        IMftSource source, IReadOnlyList<DataRun> runs, int length)
    {
        var clusterBytes = source.BytesPerCluster;

        // Neither product can overflow: the length is capped at MftAttributeList.MaximumLength,
        // and a cluster is at most 2 MiB.
        var wanted = (length + clusterBytes - 1) / clusterBytes * clusterBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(wanted);

        try
        {
            var filled = 0;

            foreach (var run in runs)
            {
                if (filled == wanted)
                {
                    break;
                }

                if (run.IsSparse)
                {
                    return null;
                }

                var take = (int)Math.Min(run.ClusterCount, (wanted - filled) / clusterBytes) * clusterBytes;

                if (!source.TryReadClusters(run.StartCluster, buffer.AsSpan(filled, take)))
                {
                    return null;
                }

                filled += take;
            }

            return filled == wanted ? MftAttributeList.TryReadEntries(buffer.AsSpan(0, length)) : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadExtensionRecords(
        IMftSource source,
        IReadOnlyList<MftDeferredRecord> deferred,
        Span<byte> batch,
        CancellationToken ct)
    {
        var wanted = new List<(MftDeferredRecord Owner, MftSegmentReference Segment, MftAttributeKinds Needs)>();

        foreach (var record in deferred)
        {
            if (record.Failed)
            {
                continue;
            }

            foreach (var (segment, needs) in record.Segments)
            {
                wanted.Add((record, segment, needs));
            }
        }

        wanted.Sort(static (a, b) => a.Segment.Record.CompareTo(b.Segment.Record));

        var bytesPerRecord = source.BytesPerRecord;
        var capacity = batch.Length / bytesPerRecord;

        // Each record is parsed from a copy. The update sequence fixup rewrites a record in place,
        // so a second owner naming the same record — only a damaged table produces one — would
        // otherwise be handed bytes the first had already fixed up, and fail their stamp check.
        var scratch = new byte[bytesPerRecord];
        var next = 0;

        while (next < wanted.Count)
        {
            ct.ThrowIfCancellationRequested();

            var first = wanted[next].Segment.Record;

            // One read from the first wanted record to the furthest the batch can reach, so records
            // close together cost one read rather than one each.
            var last = first;

            for (var i = next; i < wanted.Count && wanted[i].Segment.Record - first < capacity; i++)
            {
                last = wanted[i].Segment.Record;
            }

            var read = source.ReadBatch(first, batch[..(int)((last - first + 1) * bytesPerRecord)]);

            if (read <= 0)
            {
                // A region that cannot be read, or a record past the end of the table. Never
                // skipped past as though it had been read: the record wanted there fails, and the
                // loop tries again from the next one wanted.
                var (owner, _, needs) = wanted[next++];
                owner.Fail(needs);
                continue;
            }

            while (next < wanted.Count && wanted[next].Segment.Record - first < read)
            {
                var (owner, segment, needs) = wanted[next++];
                var offset = (int)(segment.Record - first) * bytesPerRecord;

                batch.Slice(offset, bytesPerRecord).CopyTo(scratch);
                owner.Absorb(scratch, source.BytesPerSector, segment, needs);
            }
        }
    }
}
