using System.Buffers;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Handles one record of a table. Return false to abandon the read.
/// </summary>
/// <param name="number">The record's position in the table, which is its record number.</param>
/// <param name="outcome">What the parser made of it. The three cases are not interchangeable.</param>
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
/// the pooled buffer and the short-read rule would be written twice as well.</para>
/// </summary>
internal static class MftRecordStream
{
    private const int RecordsPerBatch = 1024;

    /// <summary>
    /// Read records <c>0</c> to <paramref name="count"/> and hand each to
    /// <paramref name="onRecord"/>. Returns false if a region of the table could not be read, or if
    /// the handler asked to stop.
    ///
    /// <para>A short read is never skipped past. Advancing over it would leave a hole no later check
    /// can see: the files in the missed range simply never arrive, and every directory above them
    /// totals short with nothing to show for it. What a caller does about that is its own decision,
    /// but it always gets to make it.</para>
    /// </summary>
    public static bool TryReadAll(IMftSource source, int count, MftRecordHandler onRecord, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onRecord);

        var batchBytes = RecordsPerBatch * source.BytesPerRecord;
        var buffer = ArrayPool<byte>.Shared.Rent(batchBytes);

        try
        {
            long next = 0;

            while (next < count)
            {
                ct.ThrowIfCancellationRequested();

                var read = source.ReadBatch(next, buffer.AsSpan(0, batchBytes));
                if (read <= 0)
                {
                    return false;
                }

                for (var i = 0; i < read; i++)
                {
                    var slice = buffer.AsSpan(i * source.BytesPerRecord, source.BytesPerRecord);
                    var outcome = MftRecordParser.Parse(slice, source.BytesPerSector, out var record);

                    if (!onRecord(next + i, outcome, in record))
                    {
                        return false;
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
}
