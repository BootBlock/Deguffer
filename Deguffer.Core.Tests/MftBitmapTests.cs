using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests;

/// <summary>
/// <c>$MFT</c>'s <c>$BITMAP</c>, which decides what a pass need not read. A record it cannot speak for
/// counts as in use, so the bitmap can cost a read and never a file.
/// </summary>
public sealed class MftBitmapTests
{
    [Fact]
    public void ReadsOneBitARecordLowestBitFirst()
    {
        var bitmap = new MftBitmap([0b0000_0101, 0b1000_0000]);

        Assert.True(bitmap.IsInUse(0));
        Assert.False(bitmap.IsInUse(1));
        Assert.True(bitmap.IsInUse(2));
        Assert.False(bitmap.IsInUse(14));
        Assert.True(bitmap.IsInUse(15));
    }

    /// <summary>A table can outgrow the bits read for it, and what they do not cover has to be read.</summary>
    [Fact]
    public void CountsARecordPastItsBitsAsInUse()
    {
        var bitmap = new MftBitmap([0]);

        Assert.Equal(8, bitmap.Known);
        Assert.False(bitmap.IsInUse(7));
        Assert.True(bitmap.IsInUse(8));
        Assert.Equal(8, bitmap.NextInUse(0, 100));
        Assert.Equal(100, bitmap.NextFree(8, 100));
        Assert.Equal(92, bitmap.CountInUse(100));
    }

    /// <summary>
    /// Each answer is checked against a bit-at-a-time reading of the same bitmap, from every start to
    /// every end, so the whole-byte steps cannot skip a record or stop on the wrong kind of one.
    /// </summary>
    [Fact]
    public void FindsTheSameRecordsAsReadingEveryBit()
    {
        byte[] bits =
        [
            0, 0, 0b0001_0000, 0, 0, 0, 0b1000_0001, 0, 0, 0b0000_0010, 0, 0,
            0xFF, 0xFF, 0b1110_1111, 0xFF, 0xFF, 0b0111_1110, 0xFF, 0xFF, 0b1111_1101, 0xFF, 0xFF,
        ];
        var bitmap = new MftBitmap(bits);
        var end = bitmap.Known + 5;

        for (long from = 0; from <= end; from++)
        {
            for (var to = from; to <= end; to++)
            {
                Assert.Equal(Next(bitmap, from, to), bitmap.NextInUse(from, to));
                Assert.Equal(Free(bitmap, from, to), bitmap.NextFree(from, to));
            }

            Assert.Equal(Count(bitmap, from), bitmap.CountInUse(from));
        }
    }

    private static long Next(MftBitmap bitmap, long from, long to)
    {
        for (var record = from; record < to; record++)
        {
            if (bitmap.IsInUse(record))
            {
                return record;
            }
        }

        return to;
    }

    private static long Free(MftBitmap bitmap, long from, long to)
    {
        for (var record = from; record < to; record++)
        {
            if (!bitmap.IsInUse(record))
            {
                return record;
            }
        }

        return to;
    }

    private static long Count(MftBitmap bitmap, long end)
    {
        var count = 0L;

        for (var record = 0L; record < end; record++)
        {
            if (bitmap.IsInUse(record))
            {
                count++;
            }
        }

        return count;
    }
}
