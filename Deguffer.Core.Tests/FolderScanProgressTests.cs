using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="FolderScanProgress"/>: one running count for a scan of several folders, however many of
/// its passes report at once.
/// </summary>
public sealed class FolderScanProgressTests
{
    private sealed class Recorded : IProgress<ExploreProgress>
    {
        public List<ExploreProgress> Reports { get; } = [];

        public void Report(ExploreProgress value) => Reports.Add(value);
    }

    /// <summary>Two passes reporting in turn show one count that only grows, never each pass's own.</summary>
    [Fact]
    public void ReportsEveryPassAddedTogether()
    {
        var recorded = new Recorded();
        var sum = new FolderScanProgress(recorded);
        var first = sum.Pass();
        var second = sum.Pass();

        first.Report(new ExploreProgress(100, Total: 1000, BytesSeen: 0));
        second.Report(new ExploreProgress(30, Total: 500, BytesSeen: 0));
        first.Report(new ExploreProgress(200, Total: 1000, BytesSeen: 0));

        Assert.Equal([100L, 130, 230], recorded.Reports.Select(report => report.Done));
        Assert.Equal(1500, recorded.Reports[^1].Total);
    }

    /// <summary>A walk cannot know what it has yet to open, so a sum with one in it has no total.</summary>
    [Fact]
    public void HasNoTotalOnceAPassWithoutOneHasReported()
    {
        var recorded = new Recorded();
        var sum = new FolderScanProgress(recorded);
        var table = sum.Pass();
        var walk = sum.Pass();

        table.Report(new ExploreProgress(100, Total: 1000, BytesSeen: 0));
        walk.Report(new ExploreProgress(5, Total: null, BytesSeen: 4096));

        Assert.Equal(1000, recorded.Reports[0].Total);
        Assert.Equal(new ExploreProgress(105, Total: null, BytesSeen: 4096), recorded.Reports[1]);
    }
}
