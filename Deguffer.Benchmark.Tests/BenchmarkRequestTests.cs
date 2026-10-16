using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// What the command line accepts. A request the routes cannot honour is refused before anything is
/// timed, rather than timed as something else.
/// </summary>
public sealed class BenchmarkRequestTests
{
    [Theory]
    [InlineData("table", "C", "Table")]
    [InlineData("INDEX", "c:", "Index")]
    [InlineData("Explore", @"d:\", "Explore")]
    public void ARouteThatReadsTheTableTakesADriveInAnyOfItsForms(string route, string drive, string expected)
    {
        var request = BenchmarkRequest.Parse([route, drive], out var error);

        Assert.Null(error);
        Assert.Equal(Enum.Parse<Route>(expected), request!.Route);
        Assert.Equal(char.ToUpperInvariant(drive[0]) + @":\", request.Path);
        Assert.Equal(char.ToUpperInvariant(drive[0]), request.Drive);
        Assert.Equal(BenchmarkRequest.DefaultRuns, request.Runs);
    }

    /// <summary>The table belongs to a volume, so a folder would be timed as the whole volume.</summary>
    [Theory]
    [InlineData(@"C:\Users\testuser")]
    [InlineData("CD")]
    [InlineData("1")]
    [InlineData(@"\\server.test\share")]
    public void ARouteThatReadsTheTableRefusesAnythingButADrive(string target)
    {
        Assert.Null(BenchmarkRequest.Parse(["table", target], out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TheWalkTakesAFolderFullyQualified()
    {
        var request = BenchmarkRequest.Parse(["walk", "relative"], out _);

        Assert.Equal(Path.GetFullPath("relative"), request!.Path);
        Assert.True(Path.IsPathFullyQualified(request.Path));
    }

    /// <summary>By name only. Enum parsing would read a number or a list of names as a route.</summary>
    [Theory]
    [InlineData("scan")]
    [InlineData("1")]
    [InlineData("table, index")]
    [InlineData("")]
    public void ARouteMustBeNamed(string route)
    {
        Assert.Null(BenchmarkRequest.Parse([route, "C"], out var error));
        Assert.NotNull(error);
    }

    /// <summary>
    /// The folder Explore reads from the table is a folder, and the table's values are what it takes.
    /// A folder with no drive letter has no table to read.
    /// </summary>
    [Fact]
    public void TheFolderReadFromTheTableTakesAFolderAndTheTablesValues()
    {
        var request = BenchmarkRequest.Parse(["explorefolder", @"C:\Users\testuser", "--parse-threads", "4"], out _);

        Assert.Equal(Route.ExploreFolder, request!.Route);
        Assert.Equal(@"C:\Users\testuser", request.Path);
        Assert.Equal('C', request.Drive);
        Assert.Equal(4, request.Table.ParseThreads);
        Assert.Null(BenchmarkRequest.Parse(["explorefolder", @"C:\Users\testuser", "--threads", "4"], out _));
        Assert.Null(BenchmarkRequest.Parse(["explorefolder", @"\\server.test\share\folder"], out var error));
        Assert.Contains("drive with a letter", error, StringComparison.Ordinal);
    }

    /// <summary>The source search walks a folder, so it takes one and the walk's values.</summary>
    [Fact]
    public void TheSourceSearchTakesAFolderAndTheWalksValues()
    {
        var request = BenchmarkRequest.Parse(["sources", "relative", "--threads", "4"], out _);

        Assert.Equal(Route.Sources, request!.Route);
        Assert.Equal(Path.GetFullPath("relative"), request.Path);
        Assert.Equal(4, request.Walk.Threads);
        Assert.Null(BenchmarkRequest.Parse(["sources", "relative", "--read-size", "1024"], out _));
    }

    /// <summary>The measure route walks a folder as the walk route does, so it takes one and the walk's values.</summary>
    [Fact]
    public void TheMeasureRouteTakesAFolderAndTheWalksValues()
    {
        var request = BenchmarkRequest.Parse(["measure", "relative", "--threads", "16", "--listing-buffer", "64"], out _);

        Assert.Equal(Route.Measure, request!.Route);
        Assert.Equal(Path.GetFullPath("relative"), request.Path);
        Assert.Equal(new WalkTuning(16, 64 * 1024), request.Walk);
        Assert.Null(BenchmarkRequest.Parse(["measure", "relative", "--read-by", "name"], out _));
    }

    [Fact]
    public void TheRunsCanBeChosen() =>
        Assert.Equal(3, BenchmarkRequest.Parse(["walk", @"C:\", "--runs", "3"], out _)!.Runs);

    [Fact]
    public void TheWalksThreadsAndListingBufferCanBeChosen()
    {
        var request = BenchmarkRequest.Parse(["walk", @"C:\", "--threads", "4", "--listing-buffer", "256", "--runs", "2"], out _);

        Assert.Equal(new WalkTuning(4, 256 * 1024), request!.Walk);
        Assert.Equal(2, request.Runs);
    }

    [Fact]
    public void TheTablesReadSizeCanBeChosen() =>
        Assert.Equal(4096 * 1024, BenchmarkRequest.Parse(["table", "C", "--read-size", "4096"], out _)!.Table.ReadBytes);

    /// <summary>A size that is not whole sectors is taken as it is: the source cuts each read to whole sectors.</summary>
    [Fact]
    public void AReadSizeNeedNotBeWholeSectors() =>
        Assert.Equal(5 * 1024, BenchmarkRequest.Parse(["table", "C", "--read-size", "5"], out _)!.Table.ReadBytes);

    [Fact]
    public void ATableReadNotToldOtherwiseRunsAsAScanOfAnUnknownDriveDoes() =>
        Assert.Equal(
            VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.Unknown).Table,
            BenchmarkRequest.Parse(["table", "C"], out _)!.Table);

    [Theory]
    [InlineData("3")]
    [InlineData("16385")]
    [InlineData("big")]
    public void AReadSizeOutsideItsBoundsIsRefused(string kib)
    {
        Assert.Null(BenchmarkRequest.Parse(["table", "C", "--read-size", kib], out var error));
        Assert.NotNull(error);
    }

    /// <summary>The walk does not read the table, so a read size given to it would be reported unused.</summary>
    [Fact]
    public void TheWalkTakesNoReadSize() =>
        Assert.Null(BenchmarkRequest.Parse(["walk", @"C:\", "--read-size", "1024"], out _));

    [Fact]
    public void AWalkNotToldOtherwiseRunsAsAScanDoes() =>
        Assert.Equal(WalkTuning.Default, BenchmarkRequest.Parse(["walk", @"C:\"], out _)!.Walk);

    [Fact]
    public void TheTableReadsSizeReadsInFlightAndParseThreadsCanBeChosen()
    {
        var request = BenchmarkRequest.Parse(
            ["index", "C", "--read-size", "2048", "--reads-in-flight", "8", "--parse-threads", "2", "--runs", "2"], out _);

        Assert.Equal(new TableTuning(2048 * 1024, 8, 2), request!.Table);
        Assert.Equal(2, request.Runs);
    }

    [Theory]
    [InlineData("--reads-in-flight", "0")]
    [InlineData("--reads-in-flight", "33")]
    [InlineData("--parse-threads", "0")]
    [InlineData("--parse-threads", "65")]
    [InlineData("--parse-threads")]
    public void ATableValueOutOfRangeIsRefused(params string[] rest)
    {
        Assert.Null(BenchmarkRequest.Parse(["table", "C", .. rest], out var error));
        Assert.NotNull(error);
    }

    /// <summary>The walk reads no table, so a table value given to it would be reported unused.</summary>
    [Theory]
    [InlineData("--read-size", "1024")]
    [InlineData("--reads-in-flight", "4")]
    [InlineData("--parse-threads", "4")]
    public void TheWalkTakesNoTableValue(params string[] rest)
    {
        Assert.Null(BenchmarkRequest.Parse(["walk", @"C:\", .. rest], out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("--runs", "0")]
    [InlineData("--runs", "1001")]
    [InlineData("--runs", "-1")]
    [InlineData("--runs", "many")]
    [InlineData("--runs")]
    [InlineData("--threads", "0")]
    [InlineData("--threads", "65")]
    [InlineData("--threads")]
    [InlineData("--listing-buffer", "3")]
    [InlineData("--listing-buffer", "1025")]
    [InlineData("--depth", "4")]
    public void AnythingElseAfterTheTargetIsRefused(params string[] rest)
    {
        Assert.Null(BenchmarkRequest.Parse(["walk", @"C:\", .. rest], out var error));
        Assert.NotNull(error);
    }

    /// <summary>The table routes do not walk, so a walk value given to one would be reported unused.</summary>
    [Theory]
    [InlineData("--threads", "4")]
    [InlineData("--listing-buffer", "64")]
    public void ARouteThatReadsTheTableTakesNoWalkValue(params string[] rest)
    {
        Assert.Null(BenchmarkRequest.Parse(["table", "C", .. rest], out var error));
        Assert.NotNull(error);
    }

    /// <summary>
    /// The Explore walk takes a folder and the walk's values, and takes snapshots unless told not
    /// to, as Explore does.
    /// </summary>
    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--snapshots", "on" }, true)]
    [InlineData(new[] { "--snapshots", "off" }, false)]
    public void TheExploreWalkTakesSnapshotsUnlessToldNotTo(string[] rest, bool expected)
    {
        var request = BenchmarkRequest.Parse(["explorewalk", @"C:\Users\testuser", "--threads", "4", .. rest], out var error);

        Assert.Null(error);
        Assert.Equal(Route.ExploreWalk, request!.Route);
        Assert.Equal(@"C:\Users\testuser", request.Path);
        Assert.Equal(4, request.Walk.Threads);
        Assert.Equal(expected, request.Snapshots);
    }

    /// <summary>Only the Explore walk draws as it goes, so no other route has snapshots to switch.</summary>
    [Theory]
    [InlineData("walk", @"C:\", "on")]
    [InlineData("explore", "C", "off")]
    [InlineData("explorewalk", @"C:\", "yes")]
    [InlineData("explorewalk", @"C:\", null)]
    public void SnapshotsAreSwitchedOnTheExploreWalkAloneAndOnlyOnOrOff(string route, string target, string? value)
    {
        Assert.Null(BenchmarkRequest.Parse([route, target, "--snapshots", .. value is null ? Array.Empty<string>() : [value]], out var error));
        Assert.NotNull(error);
    }

    /// <summary>The links route takes a folder, and reads by name unless told to read through a handle, as a scan does.</summary>
    [Theory]
    [InlineData(new string[0], nameof(LinkRead.Name))]
    [InlineData(new[] { "--read-by", "name" }, nameof(LinkRead.Name))]
    [InlineData(new[] { "--read-by", "handle" }, nameof(LinkRead.Handle))]
    public void TheLinksRouteReadsByNameUnlessToldOtherwise(string[] rest, string expected)
    {
        var request = BenchmarkRequest.Parse(["links", @"C:\Users\testuser\store", .. rest], out var error);

        Assert.Null(error);
        Assert.Equal(Route.Links, request!.Route);
        Assert.Equal(@"C:\Users\testuser\store", request.Path);
        Assert.Equal(expected, request.ReadBy.ToString());
    }

    /// <summary>
    /// The links route walks with the values a scan ships with, so a walk value given to it would be
    /// reported unused, and only it reads each file.
    /// </summary>
    [Theory]
    [InlineData("links", "--threads", "4")]
    [InlineData("links", "--listing-buffer", "64")]
    [InlineData("links", "--read-by", "both")]
    [InlineData("links", "--read-by")]
    [InlineData("walk", "--read-by", "name")]
    public void TheLinksRouteAloneChoosesHowEachFileIsRead(string route, params string[] rest)
    {
        Assert.Null(BenchmarkRequest.Parse([route, @"C:\", .. rest], out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ARouteWithoutATargetIsRefused()
    {
        Assert.Null(BenchmarkRequest.Parse(["walk"], out var error));
        Assert.NotNull(error);
    }
}
