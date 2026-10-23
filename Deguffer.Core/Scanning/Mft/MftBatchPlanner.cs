namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One read a pass makes: records <see cref="First"/> up to <see cref="End"/>.
/// </summary>
/// <param name="Skipped">
/// How many records before <see cref="First"/> the bitmap showed to be free, and so were never read.
/// </param>
internal readonly record struct MftBatch(long First, long End, long Skipped)
{
    public int Count => (int)(End - First);
}

/// <summary>
/// Decides where each read of a pass starts and ends, before any of them has been made, so several
/// can be outstanding at once.
///
/// <para>Each read reaches from the first record in use to the last record in use that one read can
/// hold before a run of free records longer than <see cref="MftReadGap"/>. A shorter run inside a read
/// is read with it, because one read costs less than two either side of a short gap. A longer one
/// ends the read, and the next starts at the next record in use, so the drive never transfers a long
/// run of records nobody parses to reach the records after it. No free record is ever parsed: see
/// <see cref="MftBitmap"/>.</para>
///
/// <para>A read begins on a boundary of the largest sector, rounded down from the record in use, so a
/// volume serves it as one read rather than a record at a time. Rounding down only ever adds free
/// records to a read, and never moves which record it starts at: the start is a record number, and
/// a record is numbered by its position.</para>
/// </summary>
internal sealed class MftBatchPlanner(IMftSource source, long count, int capacity, MftBitmap? bitmap)
{
    private readonly int _alignment = Math.Max(1, NtfsBootSector.MaximumBytesPerSector / source.BytesPerRecord);
    private readonly int _gap = MftReadGap.Records(source.BytesPerRecord);

    private long _next;
    private bool _finished;

    /// <summary>
    /// The next read, or false where the table has none left. A read of no records is a region the
    /// source has no place to read from, and the last this planner gives.
    /// </summary>
    /// <param name="batch">Its <see cref="MftBatch.Skipped"/> is meaningful even where this returns false.</param>
    public bool TryNext(out MftBatch batch)
    {
        var from = _next;

        while (!_finished && _next < count)
        {
            var start = _next;
            var inUse = start;

            if (bitmap is not null)
            {
                inUse = bitmap.NextInUse(start, count);
                if (inUse >= count)
                {
                    _next = count;
                    break;
                }

                start = Math.Max(start, inUse - (inUse % _alignment));
            }

            var length = source.BatchLength(start, capacity);

            if (length > 0 && bitmap is not null)
            {
                // Nothing in use in what this read could reach, which a record read alone, where no
                // whole-sector read can serve it, can be.
                if (inUse >= start + length)
                {
                    _next = start + length;
                    continue;
                }

                var wanted = (EndOfUse(bitmap, inUse, start + length) - start + _alignment - 1) / _alignment * _alignment;
                if (wanted < length)
                {
                    length = source.BatchLength(start, (int)wanted);
                }
            }

            _finished = length == 0;
            _next = start + length;
            batch = new MftBatch(start, _next, start - from);
            return true;
        }

        _finished = true;
        batch = new MftBatch(_next, _next, _next - from);
        return false;
    }

    /// <summary>
    /// One past the last record in use, from <paramref name="inUse"/> up to <paramref name="reach"/>,
    /// that comes before the first run of free records longer than the gap. Free runs are stepped
    /// over a run at a time, not a record at a time, so a read of records nearly all in use costs a
    /// few searches of the bitmap.
    /// </summary>
    private long EndOfUse(MftBitmap bitmap, long inUse, long reach)
    {
        var record = inUse;

        while (true)
        {
            var free = bitmap.NextFree(record, reach);
            if (free >= reach)
            {
                return reach;
            }

            var next = bitmap.NextInUse(free, reach);
            if (next >= reach || next - free > _gap)
            {
                return free;
            }

            record = next;
        }
    }
}
