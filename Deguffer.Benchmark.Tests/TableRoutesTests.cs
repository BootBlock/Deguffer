using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
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
        var tally = Run(route, Volume().WithoutBitmap());

        Assert.True(tally.Complete);
        Assert.Equal(Records, tally.Items);
        Assert.Equal(Records * BytesPerRecord, tally.BytesRead);
    }

    /// <summary>
    /// A region that cannot be read stops every route short. The Explore tree is still drawn from
    /// what was read, so it is the reader saying it read part of the table that makes the run short.
    /// </summary>
    [Theory]
    [InlineData("Table")]
    [InlineData("Index")]
    [InlineData("Explore")]
    public void ATableThatStopsShortIsAnIncompleteRun(string route)
    {
        var tally = Run(route, Volume().UnreadableFrom(17).WithoutBitmap());

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
        var tally = Run("Index", Volume().CorruptSectorStamp(17).WithoutBitmap());

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
            .AddFileWithANonResidentAttributeList(17, 16, "file.bin", allocated: 8192, logical: 8000, extension: 18, listCluster: 500)
            .WithoutBitmap());

        Assert.True(tally.Complete);
        Assert.Equal(19 + 1, tally.Items);
        Assert.Equal((20 * BytesPerRecord) + 4096, tally.BytesRead);
    }

    /// <summary>
    /// The records <c>$MFT</c>'s <c>$BITMAP</c> marks free are never read, so a table read whole is a
    /// complete run with far fewer records read than it holds, and the cluster holding the bitmap is
    /// part of what it read.
    ///
    /// <para>Records 4 to 19 are read as one, from the sector boundary below the root to the one
    /// after the file at 17, and the file at 4,100 alone. The 4,080 free records between are not.</para>
    /// </summary>
    [Fact]
    public void ATableReadThroughItsBitmapIsCompleteHavingReadOnlyWhatIsInUse()
    {
        var tally = Run("Table", Volume().AddFile(4_100, 16, "far.bin", allocated: 4096, logical: 4000));

        Assert.True(tally.Complete);
        Assert.Equal(16 + 1, tally.Items);
        Assert.Equal((17 * BytesPerRecord) + 4096, tally.BytesRead);
    }

    [Fact]
    public void AVolumeThatCannotBeOpenedAgainStopsTheBenchmark() =>
        Assert.Throws<IOException>(() => TableRoutes.Run(
            Route.Table,
            FakeMftSourceFactory.Unavailable(FallbackReason.VolumeNotAddressable),
            Drive,
            TableTuning.Default,
            CancellationToken.None));

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
            Enum.Parse<Route>(route), FakeMftSourceFactory.Serving(Drive, volume), Drive, TableTuning.Default, CancellationToken.None);
}
