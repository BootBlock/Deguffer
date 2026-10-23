using System.Numerics;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Which records of a table are in use, as <c>$MFT</c>'s own <c>$BITMAP</c> says: one bit a record,
/// set where the record holds a file.
///
/// <para>It decides what need not be read, and never what a record is. The in-use flag in each
/// record's header stays the authority, so a record whose bit is set but whose header says it is
/// free is still free. A bit that is clear is believed: a record that came into use after the bitmap
/// was read is a file created during the scan, which any read of a live volume can miss.</para>
///
/// <para>A record past the bits the bitmap holds is treated as in use, so a bitmap shorter than the
/// table costs a read rather than a file.</para>
/// </summary>
public sealed class MftBitmap
{
    private readonly byte[] _bits;

    /// <param name="bits">The bitmap's bytes, record 0 in the lowest bit of the first. Held, not copied.</param>
    public MftBitmap(byte[] bits)
    {
        ArgumentNullException.ThrowIfNull(bits);

        _bits = bits;
    }

    /// <summary>How many records the bits describe. Every record from here on counts as in use.</summary>
    public long Known => (long)_bits.Length * 8;

    public bool IsInUse(long record) =>
        record >= Known || (_bits[record >> 3] & (1 << (int)(record & 7))) != 0;

    /// <summary>
    /// The first record in use from <paramref name="from"/> up to <paramref name="end"/>, or
    /// <paramref name="end"/> where none is.
    /// </summary>
    public long NextInUse(long from, long end)
    {
        var record = from;

        while (record < end && record < Known && (record & 7) != 0)
        {
            if (IsInUse(record))
            {
                return record;
            }

            record++;
        }

        if (record >= end || record >= Known)
        {
            return Math.Min(record, end);
        }

        // A byte at a time from here, which is what a long run of free records is made of.
        var limit = Math.Min(end, Known);
        var first = (int)(record >> 3);
        var at = _bits.AsSpan(first, (int)((limit - 1) >> 3) - first + 1).IndexOfAnyExcept((byte)0);

        if (at < 0)
        {
            return limit;
        }

        var found = ((long)(first + at) << 3) + BitOperations.TrailingZeroCount(_bits[first + at]);
        return Math.Min(found, end);
    }

    /// <summary>
    /// The first free record from <paramref name="from"/> up to <paramref name="end"/>, or
    /// <paramref name="end"/> where none is.
    /// </summary>
    public long NextFree(long from, long end)
    {
        var record = from;

        while (record < end && record < Known && (record & 7) != 0)
        {
            if (!IsInUse(record))
            {
                return record;
            }

            record++;
        }

        // Every record past the bits is in use, so none from there on is free.
        if (record >= end || record >= Known)
        {
            return end;
        }

        // A byte at a time from here, which is what a long run of records in use is made of.
        var limit = Math.Min(end, Known);
        var first = (int)(record >> 3);
        var at = _bits.AsSpan(first, (int)((limit - 1) >> 3) - first + 1).IndexOfAnyExcept(byte.MaxValue);

        if (at < 0)
        {
            return end;
        }

        var found = ((long)(first + at) << 3) + BitOperations.TrailingZeroCount(~_bits[first + at]);
        return found < limit ? found : end;
    }

    /// <summary>How many of records <c>0</c> up to <paramref name="end"/> are in use.</summary>
    public long CountInUse(long end)
    {
        var known = Math.Min(end, Known);
        var whole = (int)(known >> 3);
        var count = 0L;

        foreach (var bits in _bits.AsSpan(0, whole))
        {
            count += BitOperations.PopCount(bits);
        }

        for (var record = (long)whole << 3; record < known; record++)
        {
            if (IsInUse(record))
            {
                count++;
            }
        }

        return count + Math.Max(0, end - Known);
    }
}
