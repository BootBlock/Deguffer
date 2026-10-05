using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where each read of a pass starts and ends, decided before any read is made. Records here are
/// 1,024 bytes, so four make the largest sector and a read starts on a multiple of four.
/// </summary>
public sealed class MftBatchPlannerTests
{
    private const int Records = 40;

    [Fact]
    public void WithoutABitmapReadsEveryRecordInReadsOfTheSizeGiven()
    {
        Assert.Equal([new(0, 16, 0), new(16, 32, 0), new(32, 40, 0)], Plan(capacity: 16, bitmap: null, out var skipped));
        Assert.Equal(0, skipped);
    }

    /// <summary>
    /// A read starts on the sector boundary at or below the first record in use, and ends on the
    /// one after the last record in use it can reach. Rounding only ever adds free records.
    /// </summary>
    [Fact]
    public void ReadsFromTheSectorBelowTheFirstRecordInUseToTheSectorAfterTheLast()
    {
        Assert.Equal([new(4, 12, Skipped: 4)], Plan(capacity: 16, Bits(5, 9), out var skipped));
        Assert.Equal(Records - 12, skipped);
    }

    [Fact]
    public void NeverReadsARunOfFreeRecordsLongerThanARead()
    {
        Assert.Equal([new(0, 4, 0), new(28, 32, Skipped: 24)], Plan(capacity: 8, Bits(0, 30), out _));
    }

    /// <summary>One read costs less than two either side of a short gap, so the gap is read with them.</summary>
    [Fact]
    public void ReadsAShortRunOfFreeRecordsInsideARead()
    {
        Assert.Equal([new(0, 8, 0)], Plan(capacity: 16, Bits(0, 6), out _));
    }

    /// <summary>
    /// A record read alone, where no whole-sector read can serve it, can hold nothing in use even
    /// where a record after it in the same sector does. It is passed over, not read.
    /// </summary>
    [Fact]
    public void PassesOverAReadHoldingNothingInUse()
    {
        using var source = new PlannedSource((first, capacity) => first == 4 ? 1 : Math.Min(capacity, (int)(Records - first)));
        var planner = new MftBatchPlanner(source, Records, capacity: 16, Bits(6));

        Assert.True(planner.TryNext(out var batch));
        Assert.Equal(new MftBatch(5, 9, Skipped: 5), batch);
    }

    /// <summary>A region with no place to read it from ends the plan there, with a read of nothing.</summary>
    [Fact]
    public void EndsWithAReadOfNothingWhereARegionHasNoPlace()
    {
        using var source = new PlannedSource((first, capacity) => first >= 16 ? 0 : Math.Min(capacity, (int)(16 - first)));
        var planner = new MftBatchPlanner(source, Records, capacity: 8, bitmap: null);

        Assert.True(planner.TryNext(out _));
        Assert.True(planner.TryNext(out _));
        Assert.True(planner.TryNext(out var nothing));
        Assert.Equal(new MftBatch(16, 16, 0), nothing);
        Assert.False(planner.TryNext(out _));
    }

    private static List<MftBatch> Plan(int capacity, MftBitmap? bitmap, out long skippedAtEnd)
    {
        using var source = new MftFixture().AddUnused(Records - 1).WithoutBitmap().Build();
        var planner = new MftBatchPlanner(source, Records, capacity, bitmap);
        var batches = new List<MftBatch>();
        MftBatch batch;

        while (planner.TryNext(out batch))
        {
            batches.Add(batch);
        }

        skippedAtEnd = batch.Skipped;
        return batches;
    }

    private static MftBitmap Bits(params int[] inUse)
    {
        var bits = new byte[Records / 8];

        foreach (var record in inUse)
        {
            bits[record >> 3] |= (byte)(1 << (record & 7));
        }

        return new MftBitmap(bits);
    }

    /// <summary>A table whose reads are planned by <paramref name="batchLength"/> and never made.</summary>
    private sealed class PlannedSource(Func<long, int, int> batchLength) : IMftSource
    {
        public int BytesPerRecord => MftRecordBytes.BytesPerRecord;

        public long RecordCount => Records;

        public MftBitmapPlacement? Bitmap => null;

        public int BatchLength(long firstRecord, int capacity) => batchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination) => throw new NotSupportedException();

        public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
            throw new NotSupportedException();

        public int BytesPerCluster => MftRecordBytes.BytesPerCluster;

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
