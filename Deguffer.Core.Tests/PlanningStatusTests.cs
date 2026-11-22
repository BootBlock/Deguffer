using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The status line of a planning pass: what it says about the providers under way, and that it is posted
/// by time rather than on every start, end and moved figure, with the latest line always following.
/// </summary>
public sealed class PlanningStatusTests
{
    private readonly ManualTimeProvider _clock = new();
    private readonly ProgressRecorder<string> _status = new();

    [Fact]
    public void TheStatusOfManyProvidersNamesTheFirstTwoAndCountsTheRest()
    {
        PlanningStatus.Underway[] checking =
            [.. new[] { "conda", "pip", "npm", "PlatformIO" }.Select(name => new PlanningStatus.Underway(new FakeCleanupProvider(name)))];

        Assert.Equal("Checking conda…", PlanningStatus.Describe(checking[..1]));
        Assert.Equal("Checking conda and pip…", PlanningStatus.Describe(checking[..2]));
        Assert.Equal("Checking conda, pip and 2 more…", PlanningStatus.Describe(checking));
    }

    /// <summary>
    /// §5.5: a provider walking a large location says how much it has counted, so the line moves while
    /// the walk runs. Worded as a count of work done, never as what a clean would free, and left off a
    /// provider that has counted nothing yet.
    /// </summary>
    [Fact]
    public void TheStatusNamesTheBytesEachProviderHasCountedSoFar()
    {
        var npm = new PlanningStatus.Underway(new FakeCleanupProvider("npm")) { Counted = 3 * 1024 * 1024 };
        var pip = new PlanningStatus.Underway(new FakeCleanupProvider("pip"));

        Assert.Equal($"Checking npm ({FreeSpace.Format(3 * 1024 * 1024)} counted so far)…", PlanningStatus.Describe([npm]));
        Assert.Equal($"Checking pip and npm ({FreeSpace.Format(3 * 1024 * 1024)} counted so far)…", PlanningStatus.Describe([pip, npm]));
    }

    /// <summary>
    /// The line follows a provider's figure as it rises, an interval at a time, and a figure reported once
    /// the provider has ended is dropped: the finding has its own figure by then, and the line must not go
    /// on naming the provider.
    /// </summary>
    [Fact]
    public void TheStatusFollowsAProvidersFigureUntilItEnds()
    {
        using var planning = new PlanningStatus(_status, _clock);
        var npm = new FakeCleanupProvider("npm");
        var pip = new FakeCleanupProvider("pip");
        var counted = planning.Counted(npm);

        planning.Started(npm);
        planning.Started(pip);
        counted.Report(ScanSize.FromLengths(2048));
        _clock.Advance(ReportCadence.Interval);
        counted.Report(ScanSize.FromLengths(4096));
        _clock.Advance(ReportCadence.Interval);
        planning.Ended(npm);
        counted.Report(ScanSize.FromLengths(8192));
        _clock.Advance(ReportCadence.Interval);

        Assert.Equal(
            [
                "Checking npm…",
                $"Checking npm ({FreeSpace.Format(2048)} counted so far) and pip…",
                $"Checking npm ({FreeSpace.Format(4096)} counted so far) and pip…",
                "Checking pip…",
            ],
            _status.Reports);
    }

    /// <summary>
    /// A burst of starts, ends and moved figures inside one interval posts one line at its start and one
    /// at its end, rather than one for each change, and the line at the end is the one the pass's state
    /// describes.
    /// </summary>
    [Fact]
    public void ABurstOfChangesPostsTheFirstLineAndThenTheLatestOnly()
    {
        using var planning = new PlanningStatus(_status, _clock);
        var providers = Enumerable.Range(1, 8).Select(row => new FakeCleanupProvider($"row{row}")).ToArray();

        foreach (var provider in providers)
        {
            planning.Started(provider);
        }

        for (var bytes = 1024; bytes <= 64 * 1024; bytes += 1024)
        {
            foreach (var provider in providers)
            {
                planning.Counted(provider).Report(ScanSize.FromLengths(bytes));
            }
        }

        planning.Ended(providers[0]);
        planning.Ended(providers[1]);

        Assert.Equal(["Checking row1…"], _status.Reports);

        _clock.Advance(ReportCadence.Interval);

        var latest = FreeSpace.Format(64 * 1024);
        Assert.Equal(
            ["Checking row1…", $"Checking row3 ({latest} counted so far), row4 ({latest} counted so far) and 4 more…"],
            _status.Reports);
    }

    /// <summary>A figure that keeps moving is posted once an interval while it moves, and its last value follows.</summary>
    [Fact]
    public void AMovingFigureIsPostedOnceAnInterval()
    {
        using var planning = new PlanningStatus(_status, _clock);
        var npm = new FakeCleanupProvider("npm");
        var counted = planning.Counted(npm);

        planning.Started(npm);

        for (var interval = 1; interval <= 5; interval++)
        {
            for (var step = 1; step <= 10; step++)
            {
                counted.Report(ScanSize.FromLengths(((interval * 10) + step) * 1024L));
            }

            _clock.Advance(ReportCadence.Interval);
        }

        Assert.Equal(
            ["Checking npm…", .. new[] { 20, 30, 40, 50, 60 }.Select(kib => $"Checking npm ({FreeSpace.Format(kib * 1024L)} counted so far)…")],
            _status.Reports);
    }

    /// <summary>Nothing is said once the last provider ends, a report already due included: the caller's own sentence replaces the line.</summary>
    [Fact]
    public void NothingIsSaidOnceTheLastProviderEndsAReportDueIncluded()
    {
        using var planning = new PlanningStatus(_status, _clock);
        var npm = new FakeCleanupProvider("npm");

        planning.Started(npm);
        planning.Counted(npm).Report(ScanSize.FromLengths(2048));
        planning.Ended(npm);
        _clock.Advance(ReportCadence.Interval);

        Assert.Equal(["Checking npm…"], _status.Reports);
    }

    /// <summary>The pass disposes of its status as it returns, and a line due then is not posted after it.</summary>
    [Fact]
    public void NoLineIsPostedOnceDisposed()
    {
        var planning = new PlanningStatus(_status, _clock);

        planning.Started(new FakeCleanupProvider("npm"));
        planning.Started(new FakeCleanupProvider("pip"));
        planning.Dispose();
        _clock.Advance(ReportCadence.Interval);

        Assert.Equal(["Checking npm…"], _status.Reports);
    }
}
