using System.Runtime.InteropServices;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The result as it is pasted: the first run apart from the rest, and the later runs as a median
/// with their range.
/// </summary>
public sealed class ReportTests
{
    private const string Tuning = "4 folders listed at once, 64 KiB listing buffer";

    private static readonly MeasuredPlace Place = new("C:", DriveType.Fixed, "NTFS");

    private static readonly MachineFacts Machine =
        new(16, new Version(10, 0, 26100), new Version(10, 0, 0), Architecture.X64, Elevated: true, DebugBuild: false);

    [Fact]
    public void TheFirstRunIsReportedApartFromTheMedianOfTheRest()
    {
        var text = Render(Route.Walk, Run(10), Run(1), Run(3), Run(2));

        Assert.Equal("elapsed            10.000 s        2.000 s (1.000 s to 3.000 s)", Line(text, "elapsed"));
    }

    [Fact]
    public void OneRunHasNoLaterRuns()
    {
        var text = Render(Route.Walk, Run(0.25));

        Assert.Equal("elapsed            250.0 ms", Line(text, "elapsed"));
        Assert.DoesNotContain("later runs", text, StringComparison.Ordinal);
    }

    /// <summary>The walk has no bytes it can see, so it shows no rate rather than a rate of zero.</summary>
    [Fact]
    public void OnlyARouteThatReadsTheTableReportsBytesRead()
    {
        Assert.Contains("MiB/s read", Render(Route.Table, Run(2, bytes: 4L << 20)), StringComparison.Ordinal);
        Assert.DoesNotContain("MiB/s read", Render(Route.Walk, Run(2)), StringComparison.Ordinal);
    }

    [Fact]
    public void ARateIsTheItemsOverTheTime()
    {
        var text = Render(Route.Table, Run(2, items: 1_000_000, bytes: 1000L << 20));

        Assert.Equal("records/s          500,000", Line(text, "records/s"));
        Assert.Equal("MiB/s read         500.0", Line(text, "MiB/s read"));
    }

    /// <summary>A run that gave up is faster for the wrong reason, so it is counted where it shows.</summary>
    [Fact]
    public void ARunThatDidNotFinishIsCounted()
    {
        var text = Render(Route.Index, Run(1), Run(1, complete: false), Run(1));

        Assert.Equal("runs               3 (2 complete)", Line(text, "runs"));
    }

    [Fact]
    public void TheValuesTheRouteRanWithAreStated() =>
        Assert.Equal("tuning             " + Tuning, Line(Render(Route.Walk, Run(1)), "tuning"));

    /// <summary>
    /// The walk's values are the ones it was asked to run with, so two results taken at different
    /// values can never be read as one comparison.
    /// </summary>
    [Fact]
    public void TheWalksValuesAreTheOnesItRanWith() =>
        Assert.Equal(
            "3 folders listed at once, 8 KiB listing buffer",
            Deguffer.Benchmark.Tuning.Describe(Route.Walk, new WalkTuning(3, 8 * 1024), TableTuning.Default));

    [Fact]
    public void TheTablesValuesAreTheOnesItRanWith() =>
        Assert.Equal(
            "256 KiB per read, one read at a time, parsed on one thread",
            Deguffer.Benchmark.Tuning.Describe(Route.Table, WalkTuning.Default, new TableTuning(256 * 1024)));

    [Fact]
    public void ADebugBuildIsCalledOut()
    {
        var text = Report.Render(Route.Walk, Tuning, Place, Machine with { DebugBuild = true }, [Run(1)]);

        Assert.StartsWith("warning ", Line(text, "warning"), StringComparison.Ordinal);
        Assert.DoesNotContain("warning", Render(Route.Walk, Run(1)), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePlaceIsTheDriveAndItsFileSystem() =>
        Assert.Equal("place              C: (Fixed, NTFS)", Line(Render(Route.Walk, Run(1)), "place"));

    private static string Render(Route route, params RunSample[] runs) => Report.Render(route, Tuning, Place, Machine, runs);

    private static RunSample Run(double seconds, long items = 100, long bytes = 0, bool complete = true) =>
        new(TimeSpan.FromSeconds(seconds), new RunTally(items, bytes, complete), AllocatedBytes: 1 << 20, PeakWorkingSet: 64 << 20);

    private static string Line(string text, string label) =>
        text.Split('\n').Single(line => line.StartsWith(label + " ", StringComparison.Ordinal));
}
