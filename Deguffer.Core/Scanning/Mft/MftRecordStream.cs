namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Handles one record of a table. Return false to abandon the read.
/// </summary>
/// <param name="number">The record's position in the table, which is its record number.</param>
/// <param name="outcome">
/// What the parser made of it. The three cases are not interchangeable, and
/// <see cref="MftParseOutcome.Continued"/> is never one of them.
/// </param>
/// <param name="record">Meaningful only where <paramref name="outcome"/> is
/// <see cref="MftParseOutcome.Parsed"/>.</param>
internal delegate bool MftRecordHandler(long number, MftParseOutcome outcome, in MftRecord record);

/// <summary>
/// Reads a table from end to end in batches, parsing each record and handing it on.
///
/// <para>This is the byte-level half of reading an MFT, and nothing else. It holds no opinion about
/// what an unreadable record means, what to keep, or when to give up — those are policy, they
/// differ between the callers, and putting them here is what would make one caller's requirement
/// able to change the other's answer.</para>
///
/// <para>Two callers need it and they want opposite things.
/// <see cref="MftVolumeIndexBuilder"/> abandons the volume rather than report a total that is
/// short, because its numbers decide deletions. <see cref="Exploring.MftExploreReader"/> keeps
/// going and marks what it missed, because its numbers draw a picture. Written twice, the batching,
/// the aligned buffer and the short-read rule would be written twice as well.</para>
/// </summary>
internal static class MftRecordStream
{
    /// <summary>Internal rather than private so a benchmark result can state the value it measured.</summary>
    internal const int RecordsPerBatch = 1024;

    /// <summary>
    /// Read records <c>0</c> to <paramref name="count"/> and hand each to
    /// <paramref name="onRecord"/>. Returns false if a region of the table could not be read, or if
    /// the handler asked to stop.
    ///
    /// <para>A short read is never skipped past. Advancing over it would leave a hole no later check
    /// can see: the files in the missed range simply never arrive, and every directory above them
    /// totals short with nothing to show for it. What a caller does about that is its own decision,
    /// but it always gets to make it.</para>
    ///
    /// <para>A record whose attributes continue in extension records is held back and handed on
    /// after the first pass, once <see cref="MftExtensionReader"/> has read what it needs, so
    /// records do not all arrive in table order. Those held back are still handed on where the
    /// first pass stopped at a region it could not read, because a caller that keeps going has a
    /// use for every record that was read.</para>
    ///
    /// <para>A table that wants more extension records than it holds is not one NTFS wrote: each
    /// extension record belongs to one base record, so the wants of a healthy table sum to fewer
    /// than its records. Such a table is treated as one that could not be read in full. Holding
    /// every want of a hostile table would grow without bound, and running out of memory is a
    /// failure no caller can fall back from.</para>
    /// </summary>
    public static bool TryReadAll(IMftSource source, int count, MftRecordHandler onRecord, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onRecord);

        // Aligned because a volume source reads straight into it. See VolumeReadBuffer for why.
        using var buffer = new VolumeReadBuffer(RecordsPerBatch * source.BytesPerRecord);
        var batch = buffer.Span;
        var deferred = new List<MftDeferredRecord>();

        long next = 0;
        long wanted = 0;
        var wholeTable = true;

        while (wholeTable && next < count)
        {
            ct.ThrowIfCancellationRequested();

            var read = source.ReadBatch(next, batch);
            if (read <= 0)
            {
                wholeTable = false;
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var slice = batch.Slice(i * source.BytesPerRecord, source.BytesPerRecord);
                var outcome = MftRecordParser.Parse(slice, next + i, out var record, out var continued);

                if (continued is not null)
                {
                    wanted += continued.Segments.Count;
                    if (wanted > count)
                    {
                        wholeTable = false;
                        break;
                    }

                    deferred.Add(continued);
                    continue;
                }

                if (!onRecord(next + i, outcome, in record))
                {
                    return false;
                }
            }

            next += read;
        }

        wholeTable &= MftExtensionReader.TryResolve(source, deferred, count - wanted, batch, ct);

        foreach (var held in deferred)
        {
            var outcome = held.Finish(out var record);

            if (!onRecord(held.Self.Record, outcome, in record))
            {
                return false;
            }
        }

        return wholeTable;
    }
}
