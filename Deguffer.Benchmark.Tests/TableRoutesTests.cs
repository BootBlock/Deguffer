using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// A run that gave up early is faster for the wrong reason, so a table route reports a run as
/// complete only where it read the whole table and its own route kept everything it read.
/// </summary>
public sealed class TableRoutesTests
{
    private const char Drive = 'C';

    private const int Records = 18;

    private const int BytesPerRecord = 1024;

    [Theory]
    [InlineData("Table")]
    [InlineData("Index")]
    [InlineData("Explore")]
    public void AWholeTableIsACompleteRun(string route)
    {
        var tally = Run(route, Volume());

        Assert.True(tally.Complete);
        Assert.Equal(Records, tally.Items);
        Assert.Equal(Records * BytesPerRecord, tally.BytesRead);
    }

    /// <summary>
    /// A region that cannot be read stops every route short. The Explore tree is still drawn from
    /// what was read, so only the count of records read can say it is short.
    /// </summary>
    [Theory]
    [InlineData("Table")]
    [InlineData("Index")]
    [InlineData("Explore")]
    public void ATableThatStopsShortIsAnIncompleteRun(string route)
    {
        var tally = Run(route, Volume().UnreadableFrom(17));

        Assert.False(tally.Complete);
        Assert.Equal(17, tally.Items);
        Assert.Equal(17 * BytesPerRecord, tally.BytesRead);
    }

    /// <summary>
    /// The index abandons a volume holding a record it cannot place, after reading every record. The
    /// run read the whole table and is still incomplete, because the index it timed was never built.
    /// </summary>
    [Fact]
    public void AnIndexThatAbandonsTheVolumeIsAnIncompleteRun()
    {
        var tally = Run("Index", Volume().CorruptSectorStamp(17));

        Assert.False(tally.Complete);
        Assert.Equal(Records, tally.Items);
    }

    /// <summary>
    /// Following an attribute list is part of the read. A file whose list is kept outside the table
    /// costs the clusters holding the list and a second read of the extension record it names, and
    /// a rate that left either out would credit the run with a speed it did not have.
    /// </summary>
    [Fact]
    public void CountsWhatFollowingAnAttributeListRead()
    {
        var tally = Run("Table", new MftFixture()
            .AddDirectory(16, 5, "folder")
            .AddFileWithANonResidentAttributeList(17, 16, "file.bin", allocated: 8192, logical: 8000, extension: 18, listCluster: 500));

        Assert.True(tally.Complete);
        Assert.Equal(19 + 1, tally.Items);
        Assert.Equal((20 * BytesPerRecord) + 4096, tally.BytesRead);
    }

    [Fact]
    public void AVolumeThatCannotBeOpenedAgainStopsTheBenchmark() =>
        Assert.Throws<IOException>(() => TableRoutes.Run(
            Route.Table, FakeMftSourceFactory.Unavailable(FallbackReason.VolumeNotAddressable), Drive, CancellationToken.None));

    [Fact]
    public void TheProbeGivesTheReasonAVolumeCannotBeRead() =>
        Assert.Equal(
            FallbackReason.NotElevated,
            TableRoutes.Probe(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), Drive));

    /// <summary>The reserved records, a folder under the root, and a file in it: records 0 to 17.</summary>
    private static MftFixture Volume() =>
        new MftFixture()
            .AddDirectory(16, 5, "folder")
            .AddFile(17, 16, "file.bin", allocated: 4096, logical: 4000);

    private static RunTally Run(string route, MftFixture volume) =>
        TableRoutes.Run(
            Enum.Parse<Route>(route), FakeMftSourceFactory.Serving(Drive, volume), Drive, CancellationToken.None);
}
