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

    /// <summary>
    /// The walk says how many folders it listed at once, so a run below its thread count shows as
    /// one. The table lists no folders, so it shows no count rather than a count of zero.
    /// </summary>
    [Fact]
    public void OnlyTheWalkReportsTheFoldersItListedAtOnce()
    {
        var text = Render(Route.Walk, Run(1, peak: 16), Run(1, peak: 12), Run(1, peak: 14), Run(1, peak: 10));

        Assert.Equal("folders at once    16              12 (10 to 14)", Line(text, "folders at once"));
        Assert.DoesNotContain("folders at once", Render(Route.Table, Run(2)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A route that builds what Explore keeps on screen says what that keeps, which the peak working
    /// set cannot. A route that keeps nothing shows no figure rather than a figure of zero.
    /// </summary>
    [Fact]
    public void OnlyARouteThatKeepsWhatItBuiltReportsWhatItKeeps()
    {
        var text = Render(Route.ExploreFolder, Run(1, kept: 8L << 20), Run(1, kept: 2L << 20), Run(1, kept: 4L << 20));

        Assert.Equal("kept               8.0 MiB         3.0 MiB (2.0 MiB to 4.0 MiB)", Line(text, "kept"));
        Assert.Contains("kept ", Render(Route.Explore, Run(1)), StringComparison.Ordinal);
        Assert.Contains("kept ", Render(Route.ExploreWalk, Run(1)), StringComparison.Ordinal);
        Assert.DoesNotContain("kept ", Render(Route.Table, Run(1)), StringComparison.Ordinal);
        Assert.DoesNotContain("kept ", Render(Route.Walk, Run(1)), StringComparison.Ordinal);
    }

    [Fact]
    public void ARateIsTheItemsOverTheTime()
    {
        var text = Render(Route.Table, Run(2, items: 1_000_000, bytes: 1000L << 20));

        Assert.Equal("records/s          500,000", Line(text, "records/s"));
        Assert.Equal("MiB/s read         500.0", Line(text, "MiB/s read"));
    }

    /// <summary>
    /// How many processors a run kept busy is its processor time over its elapsed time, so a run held
    /// up by the drive shows as one that used little of the machine.
    /// </summary>
    [Fact]
    public void ProcessorsBusyIsTheProcessorTimeOverTheElapsedTime()
    {
        var text = Render(
            Route.Table, Run(2, processorSeconds: 1), Run(2, processorSeconds: 6), Run(2, processorSeconds: 2), Run(4, processorSeconds: 1));

        Assert.Equal("processors busy    0.50            1.00 (0.25 to 3.00)", Line(text, "processors busy"));
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
            Deguffer.Benchmark.Tuning.Describe(Route.Walk, new WalkTuning(3, 8 * 1024), TableTuning.Default, snapshots: true, LinkRead.Name));

    /// <summary>
    /// The Explore walk also states whether it took snapshots, which is the value it is timed both
    /// ways with, so the two results cannot be mistaken for each other.
    /// </summary>
    [Theory]
    [InlineData(true, "3 folders listed at once, 8 KiB listing buffer, snapshots on")]
    [InlineData(false, "3 folders listed at once, 8 KiB listing buffer, snapshots off")]
    public void TheExploreWalkSaysWhetherItTookSnapshots(bool snapshots, string expected) =>
        Assert.Equal(
            expected,
            Deguffer.Benchmark.Tuning.Describe(Route.ExploreWalk, new WalkTuning(3, 8 * 1024), TableTuning.Default, snapshots, LinkRead.Name));

    /// <summary>
    /// The Explore walk counts what its walk listed, as the walk does, so the two results name the
    /// same thing per second.
    /// </summary>
    [Fact]
    public void TheExploreWalkCountsEntries() =>
        Assert.Contains("entries/s", Render(Route.ExploreWalk, Run(1)), StringComparison.Ordinal);

    /// <summary>The same for a table route, whose values are the reads and the parse threads.</summary>
    [Fact]
    public void ATableRoutesValuesAreTheOnesItRanWith() =>
        Assert.Equal(
            "2048 KiB reads, 8 in flight, parsed on 3 threads",
            Deguffer.Benchmark.Tuning.Describe(Route.Index, WalkTuning.Default, new TableTuning(2048 * 1024, 8, 3), snapshots: true, LinkRead.Name));

    [Fact]
    public void ADebugBuildIsCalledOut()
    {
        var text = Report.Render(Route.Walk, Tuning, [Place], Machine with { DebugBuild = true }, [Run(1)]);

        Assert.StartsWith("warning ", Line(text, "warning"), StringComparison.Ordinal);
        Assert.DoesNotContain("warning", Render(Route.Walk, Run(1)), StringComparison.Ordinal);
    }

    /// <summary>The folders route measures on every drive its folders are on, and names each.</summary>
    [Fact]
    public void SeveralPlacesAreEachNamed() =>
        Assert.Equal(
            "places             C: (Fixed, NTFS), D: (Fixed, NTFS)",
            Line(Report.Render(Route.Folders, Tuning, [Place, Place with { Root = "D:" }], Machine, [Run(1)]), "places"));

    /// <summary>The folders route runs with what a search would use on each drive, not with values the command line gave.</summary>
    [Fact]
    public void TheFoldersRouteSaysItRanWithASearchsValues() =>
        Assert.Equal(
            "the values a search uses for each drive's kind, under the shipped settings",
            Deguffer.Benchmark.Tuning.Describe(Route.Folders, new WalkTuning(3, 8 * 1024), TableTuning.Default, snapshots: true, LinkRead.Name));

    [Fact]
    public void ThePlaceIsTheDriveAndItsFileSystem() =>
        Assert.Equal("place              C: (Fixed, NTFS)", Line(Render(Route.Walk, Run(1)), "place"));

    private static string Render(Route route, params RunSample[] runs) => Report.Render(route, Tuning, [Place], Machine, runs);

    private static RunSample Run(
        double seconds, long items = 100, long bytes = 0, bool complete = true, int peak = 0, long kept = 0, double processorSeconds = 0) =>
        new(
            TimeSpan.FromSeconds(seconds), new RunTally(items, bytes, complete, peak), AllocatedBytes: 1 << 20, FullCollections: 0,
            PeakWorkingSet: 64 << 20, TimeSpan.FromSeconds(processorSeconds), kept);

    private static string Line(string text, string label) =>
        text.Split('\n').Single(line => line.StartsWith(label + " ", StringComparison.Ordinal));
}
