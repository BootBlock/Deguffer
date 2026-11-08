using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// What the race route accepts. It reproduces the race a scan runs, so where it is told nothing it
/// walks and builds with what a scan uses for each drive's kind, and an option still wins over that.
/// </summary>
public sealed class BenchmarkRequestRaceTests
{
    private const byte Nvme = 0x11;
    private const byte Sata = 0x0B;

    /// <summary>C: on an NVMe disk, D: on a solid-state SATA disk: two kinds Auto reads the table differently on.</summary>
    private static readonly ScanTuner Machine = new(
        new FakePreferences(AppPreferences.Default),
        new VolumeMediaCache(new FakeStorageQueries()
            .Volume(@"C:\", 0).Disk(0, Nvme, seekPenalty: false)
            .Volume(@"D:\", 1).Disk(1, Sata, seekPenalty: false)),
        new FakeVolumeInventory().With(@"C:\").With(@"D:\"));

    [Fact]
    public void TheRaceBuildsTheFoldersOwnVolumeWithWhatAScanUsesForItsKind()
    {
        var request = Parse("race", @"C:\Users\testuser\source");

        var nvme = VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.Nvme);
        Assert.Equal(Route.Race, request.Route);
        Assert.Equal(@"C:\Users\testuser\source", request.Path);
        Assert.Equal('C', request.Drive);
        Assert.Equal(nvme.Table, request.Table);
        Assert.Equal(nvme.Walk, request.Walk);

        // What makes the default a drive kind's, not the unknown drive's every other table route uses.
        Assert.NotEqual(BenchmarkRequest.UnknownDrive.ReadsInFlight, request.Table.ReadsInFlight);
    }

    /// <summary>The build is of the volume named, so it reads with that drive's values, not the folder's.</summary>
    [Fact]
    public void ABuildOfAnotherVolumeReadsWithThatVolumesValues()
    {
        var request = Parse("race", @"C:\Users\testuser\source", "--build", "d:");

        Assert.Equal('D', request.Drive);
        Assert.Equal(VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.SolidState).Table, request.Table);
    }

    [Fact]
    public void AnOptionWinsOverTheDrivesKind()
    {
        var request = Parse("race", @"C:\Users\testuser\source", "--reads-in-flight", "2", "--read-size", "256", "--threads", "4");

        Assert.Equal(2, request.Table.ReadsInFlight);
        Assert.Equal(256 * 1024, request.Table.ReadBytes);
        Assert.Equal(4, request.Walk.Threads);
    }

    [Fact]
    public void TheRaceSearchesForBuildOutputsWithNothingBetweenPartsUnlessToldOtherwise()
    {
        var plain = Parse("race", @"C:\Users\testuser\source");
        var told = Parse("race", @"C:\Users\testuser\source", "--before-part", "empty-cache.cmd --all", "--racer", "walk");

        Assert.Equal(Route.Sources, plain.Racer);
        Assert.Null(plain.BeforePart);
        Assert.Equal(Route.Walk, told.Racer);
        Assert.Equal("empty-cache.cmd --all", told.BeforePart);
    }

    /// <summary>The build yields to the walk, as Auto's does, unless told to read at normal priority throughout.</summary>
    [Fact]
    public void TheRaceBuildYieldsUnlessToldOtherwise()
    {
        Assert.True(Parse("race", @"C:\Users\testuser\source").BuildYields);
        Assert.False(Parse("race", @"C:\Users\testuser\source", "--build-priority", "normal").BuildYields);
        Assert.True(Parse("race", @"C:\Users\testuser\source", "--build-priority", "yield").BuildYields);
    }

    [Theory]
    [InlineData("race", "--build-priority", "low")]
    [InlineData("race", "--build-priority")]
    [InlineData("index", "--build-priority", "yield")]
    [InlineData("race", "--racer", "index")]
    [InlineData("race", "--build", @"D:\folder")]
    [InlineData("race", "--build")]
    [InlineData("race", "--before-part", " ")]
    [InlineData("race", "--before-part")]
    [InlineData("walk", "--before-part", "empty-cache.cmd")]
    [InlineData("walk", "--build", "D")]
    [InlineData("index", "--racer", "walk")]
    public void AnOptionTheRouteCannotHonourIsRefused(string route, string option, string? value = null)
    {
        string[] args = value is null ? [route, @"C:\Users\testuser\source", option] : [route, @"C:\Users\testuser\source", option, value];

        Assert.Null(BenchmarkRequest.Parse(args, Machine, out var error));
        Assert.NotNull(error);
    }

    /// <summary>The build reads a volume's table, so a folder reached by no drive letter cannot be raced.</summary>
    [Fact]
    public void AFolderWithNoDriveLetterIsRefused()
    {
        Assert.Null(BenchmarkRequest.Parse(["race", @"\\server.test\share\source"], Machine, out var error));
        Assert.NotNull(error);
    }

    private static BenchmarkRequest Parse(params string[] args)
    {
        var request = BenchmarkRequest.Parse(args, Machine, out var error);

        Assert.Null(error);
        return request!;
    }
}
