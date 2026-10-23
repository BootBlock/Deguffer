namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The second pass over a table: completes the first pass's <see cref="MftDeferredRecord"/>s from
/// what the extension records they want hold.
///
/// <para>A second pass rather than a lookup in the middle of the first, because the first meets an
/// extension record before its owner as often as after it. The first pass keeps what each extension
/// record holding something the scan needs holds as it reads it, so an extension record it reached
/// is never read again: every wanted one is collected, sorted by record number, and merged against
/// what was kept.
/// The disk is read again only for the attribute lists kept outside the table, and for a wanted
/// record the first pass never reached because it stopped first.</para>
///
/// <para>Bytes only, like <see cref="MftRecordStream"/>. What a record that could not be completed
/// means is for the caller to decide, and each <see cref="MftDeferredRecord"/> already says which
/// it is.</para>
/// </summary>
internal static class MftExtensionReader
{
    private const MftAttributeKinds Everything =
        MftAttributeKinds.Name | MftAttributeKinds.DataStart | MftAttributeKinds.ReparsePoint | MftAttributeKinds.WofDataStart;

    /// <summary>
    /// Complete every record the first pass deferred. Returns false where the lists read here want
    /// more extension records than <paramref name="budget"/> allows, which a table NTFS wrote never
    /// does; the records past that point are then lost whole rather than followed.
    /// </summary>
    /// <param name="budget">
    /// How many more extension records the table can hold for these records to want, after the
    /// first pass's own.
    /// </param>
    /// <param name="readsInFlight">How many lists kept outside the table are read at once, at most.</param>
    /// <param name="batch">
    /// A buffer of whole records to read a record the first pass never reached into, lent by the
    /// caller so the second pass reuses the first pass's pooled buffer rather than renting another.
    /// </param>
    public static bool TryResolve(
        IMftSource source,
        MftFirstPass first,
        long budget,
        int readsInFlight,
        Span<byte> batch,
        CancellationToken ct)
    {
        // The first pass's own wants may already have spent it, and then no list is read at all.
        if (budget >= 0)
        {
            MftAttributeListReader.Read(source, first.Deferred, readsInFlight, (record, entries) =>
            {
                record.Follow(entries);
                budget -= record.Segments.Count;
                return budget >= 0;
            }, ct);
        }

        if (budget < 0)
        {
            foreach (var record in first.Deferred)
            {
                if (record.PendingList is not null)
                {
                    record.Fail(Everything);
                }
            }
        }

        var wanted = Wanted(first.Deferred);
        var unreached = Absorb(wanted, first.Extensions, first.Reached);

        ReadUnreached(source, wanted, unreached, batch, ct);
        return budget >= 0;
    }

    /// <summary>
    /// The entries of a list kept outside the table, or null where any part of it cannot be read.
    /// How <see cref="MftExtentMapReader"/> reads <c>$MFT</c>'s own list, before there is a source to
    /// read anything else through, which is why it takes a <see cref="ClusterReader"/>.
    /// </summary>
    /// <param name="length">At most <see cref="MftAttributeList.MaximumLength"/>.</param>
    internal static IReadOnlyList<MftAttributeListEntry>? TryReadList(
        ClusterReader read, int clusterBytes, IReadOnlyList<DataRun> runs, int length) =>
        MftClusterValue.TryRead(read, clusterBytes, runs, length) is { } value
            ? MftAttributeList.TryReadEntries(value)
            : null;

    /// <summary>Every extension record a record still able to be completed wants, in record order.</summary>
    private static List<(MftDeferredRecord Owner, MftSegmentReference Segment, MftAttributeKinds Needs)> Wanted(
        IReadOnlyList<MftDeferredRecord> deferred)
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
        return wanted;
    }

    /// <summary>
    /// Fold in every wanted record the first pass reached, from what it kept. One it reached and kept
    /// nothing for was free, a base record, unreadable, or held nothing the scan needs, and the record
    /// wanting it fails. Returns where the wanted records the first pass never reached begin.
    /// </summary>
    private static int Absorb(
        List<(MftDeferredRecord Owner, MftSegmentReference Segment, MftAttributeKinds Needs)> wanted,
        IReadOnlyList<MftExtensionRecord> extensions,
        long reached)
    {
        var kept = 0;
        var next = 0;

        for (; next < wanted.Count && wanted[next].Segment.Record < reached; next++)
        {
            var (owner, segment, needs) = wanted[next];

            while (kept < extensions.Count && extensions[kept].Number < segment.Record)
            {
                kept++;
            }

            owner.Absorb(
                kept < extensions.Count && extensions[kept].Number == segment.Record ? extensions[kept] : null,
                segment,
                needs);
        }

        return next;
    }

    /// <summary>
    /// Read the wanted records from <paramref name="next"/> on, which the first pass stopped before
    /// reaching, and fold each in.
    /// </summary>
    private static void ReadUnreached(
        IMftSource source,
        List<(MftDeferredRecord Owner, MftSegmentReference Segment, MftAttributeKinds Needs)> wanted,
        int next,
        Span<byte> batch,
        CancellationToken ct)
    {
        var bytesPerRecord = source.BytesPerRecord;
        var capacity = batch.Length / bytesPerRecord;
        var gap = MftReadGap.Records(bytesPerRecord);

        while (next < wanted.Count)
        {
            ct.ThrowIfCancellationRequested();

            var first = wanted[next].Segment.Record;

            // One read from the first wanted record to the furthest the batch can reach, so records
            // close together cost one read rather than one each. Only while they are close: a run of
            // unwanted records longer than MftReadGap ends the read, or a large buffer would read
            // megabytes of records nobody wants to reach two that somebody does.
            var last = first;

            for (var i = next;
                 i < wanted.Count && wanted[i].Segment.Record - first < capacity && wanted[i].Segment.Record - last - 1 <= gap;
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

            // Each record is read once however many owners name it, and only a damaged table has two
            // owners naming one: the fixup rewrites a record in place, so it cannot be run twice.
            MftExtensionRecord? found = null;
            var foundAt = -1L;

            while (next < wanted.Count && wanted[next].Segment.Record - first < read)
            {
                var (owner, segment, needs) = wanted[next++];

                if (segment.Record != foundAt)
                {
                    foundAt = segment.Record;
                    found = MftExtensionRecord.TryRead(
                        segment.Record, batch.Slice((int)(segment.Record - first) * bytesPerRecord, bytesPerRecord));
                }

                owner.Absorb(found, segment, needs);
            }
        }
    }
}
