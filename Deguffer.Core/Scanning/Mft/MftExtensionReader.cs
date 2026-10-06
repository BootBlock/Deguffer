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
    /// The longest run of unwanted records one read of the second pass reads across rather than
    /// making two reads: sixty-four of the usual 1,024-byte records. Reading across a short gap saves
    /// a second read, and reading across a long one brings records nobody wants.
    /// </summary>
    private const int GapBytes = 64 * 1024;

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
        ClusterReader read = source.TryReadClusters;

        foreach (var record in deferred)
        {
            ct.ThrowIfCancellationRequested();

            if (record.PendingList is not { } list)
            {
                continue;
            }

            if (budget < 0)
            {
                record.Fail(
                    MftAttributeKinds.Name | MftAttributeKinds.DataStart | MftAttributeKinds.ReparsePoint | MftAttributeKinds.WofDataStart);
                continue;
            }

            record.Follow(TryReadList(read, source.BytesPerCluster, list.Runs, list.Length));
            budget -= record.Segments.Count;
        }

        ReadExtensionRecords(source, deferred, batch, ct);
        return budget >= 0;
    }

    /// <summary>
    /// The entries of a list kept outside the table, or null where any part of it cannot be read.
    ///
    /// <para>Also how <see cref="MftExtentMapReader"/> reads <c>$MFT</c>'s own list, which is why
    /// it takes a <see cref="ClusterReader"/> rather than a source.</para>
    /// </summary>
    /// <param name="length">At most <see cref="MftAttributeList.MaximumLength"/>.</param>
    internal static IReadOnlyList<MftAttributeListEntry>? TryReadList(
        ClusterReader read, int clusterBytes, IReadOnlyList<DataRun> runs, int length) =>
        MftClusterValue.TryRead(read, clusterBytes, runs, length) is { } value
            ? MftAttributeList.TryReadEntries(value)
            : null;

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
        var gapRecords = Math.Max(1, GapBytes / bytesPerRecord);

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
            // close together cost one read rather than one each. Only while they are close: a gap
            // longer than GapBytes ends the read, or a large buffer would read megabytes of records
            // nobody wants to reach two that somebody does.
            var last = first;

            for (var i = next;
                 i < wanted.Count && wanted[i].Segment.Record - first < capacity && wanted[i].Segment.Record - last <= gapRecords;
                 i++)
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
                owner.Absorb(scratch, segment, needs);
            }
        }
    }
}
