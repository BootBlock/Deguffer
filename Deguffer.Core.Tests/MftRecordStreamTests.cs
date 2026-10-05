using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the record stream says about the table as a whole, apart from what either caller makes of
/// the records it hands on.
/// </summary>
public sealed class MftRecordStreamTests
{
    /// <summary>
    /// Each extension record belongs to one base record, so a table whose lists want more of them
    /// than it holds was not written by NTFS. Following every want of a hostile one would hold
    /// memory without bound, so the stream stops and says the table was not read in full, which
    /// both callers already know how to fall back from. Inside a record the wants are counted in
    /// the first pass, and outside one in the second.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaysATableWantingMoreExtensionRecordsThanItHoldsWasNotReadInFull(bool outsideTheRecord)
    {
        using var source = new MftFixture()
            .AddFileWhoseListWantsMoreRecordsThanTheTableHolds(20, outsideTheRecord)
            .Build();

        Assert.True(source.RecordCount < 24, "the fixture's list must name more records than the table holds");
        Assert.False(MftRecordStream.TryReadAll(source, (int)source.RecordCount, TableTuning.Default, static (_, _, in _) => true, default));
    }

    /// <summary>
    /// Where the first pass meets the record that takes the wants past the table's size, it stops
    /// there rather than reading on and holding the wants of every record after it.
    /// </summary>
    [Fact]
    public void StopsReadingAtTheRecordWhoseWantsOutgrowTheTable()
    {
        using var source = new MftFixture()
            .AddFileWhoseListWantsMoreRecordsThanTheTableHolds(20, outsideTheRecord: false)
            .AddFile(21, MftRecord.RootRecordNumber, "after.tgz", allocated: 4096, logical: 4000)
            .Build();
        var handed = new List<long>();

        MftRecordStream.TryReadAll(source, (int)source.RecordCount, TableTuning.Default, (number, _, in _) =>
        {
            handed.Add(number);
            return true;
        }, default);

        Assert.Contains(19L, handed);
        Assert.DoesNotContain(21L, handed);
    }

    /// <summary>The same stream reads a table whose lists want only what it holds to its end.</summary>
    [Fact]
    public void ReadsATableWhoseListsWantOnlyWhatItHolds()
    {
        using var source = new MftFixture()
            .AddFileWithDataInAnExtensionRecord(20, MftRecord.RootRecordNumber, "fragmented.tgz", allocated: 8192, logical: 8000, extension: 21)
            .Build();

        Assert.True(MftRecordStream.TryReadAll(source, (int)source.RecordCount, TableTuning.Default, static (_, _, in _) => true, default));
    }
}
