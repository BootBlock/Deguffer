using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// A rate is worked out from what was read, not from the size of the table, so a read that stopped
/// short is not credited with bytes it never read.
/// </summary>
public sealed class CountingMftSourceTests
{
    private const int BytesPerRecord = 1024;

    [Fact]
    public void CountsTheRecordsEachReadReturned()
    {
        using var source = new CountingMftSource(Table(records: 10, unreadableFrom: 10));
        var buffer = new byte[4 * BytesPerRecord];

        Assert.Equal(4, source.ReadBatch(0, buffer));
        Assert.Equal(4, source.ReadBatch(4, buffer));
        Assert.Equal(2, source.ReadBatch(8, buffer));

        Assert.Equal(10, source.RecordsRead);
        Assert.Equal(10 * BytesPerRecord, source.BytesRead);
    }

    [Fact]
    public void ARegionThatCannotBeReadAddsNothing()
    {
        using var source = new CountingMftSource(Table(records: 10, unreadableFrom: 3));
        var buffer = new byte[8 * BytesPerRecord];

        Assert.Equal(3, source.ReadBatch(0, buffer));
        Assert.Equal(0, source.ReadBatch(3, buffer));

        Assert.Equal(3, source.RecordsRead);
        Assert.Equal(10, source.RecordCount);
    }

    private static FixtureMftSource Table(int records, long unreadableFrom) =>
        new([.. Enumerable.Range(0, records).Select(_ => new byte[BytesPerRecord])], 512, BytesPerRecord, unreadableFrom);
}
