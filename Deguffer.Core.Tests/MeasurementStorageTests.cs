using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A measurement asks what its paths are on once for each drive, not once for each path (#1065).
/// Before it walks, a measurement places its paths into pipelines by disk, registers its walks with
/// the index builds that yield to them, and chooses each walk's values, and all of those read one set
/// of answers. The questions are counted where the machine answers them,
/// <see cref="IVolumeInventory.SubstituteOf"/>, which is one <c>QueryDosDevice</c> a question.
/// </summary>
public sealed class MeasurementStorageTests : IDisposable
{
    private const byte Nvme = 0x11;
    private const byte Sata = 0x0B;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AWalkingMeasurementOfAThousandDirectoriesAsksTheirDriveOnce()
    {
        var (tuner, inventory) = OnOneDrive(_temp.Path);
        var asked = Directories(1000);

        var results = await new ParallelEnumerationScanner(tuner).MeasureManyAsync(asked).AsTask().WaitAsync(Bound);

        Assert.All(results, result => Assert.True(result.WasReached));
        Assert.Equal(1, inventory.SubstituteQueries);
    }

    /// <summary>
    /// Through the scanner that chooses each path's route, with a table that will not open: every path
    /// is left to the walk, which is registered with the builds and planned through the answers the
    /// measurement made at its start. Asking for the volume's table asks about its drive once, which is
    /// per volume, so the count is compared with a measurement of two paths rather than fixed.
    /// </summary>
    [Fact]
    public async Task AMeasurementThatChoosesARouteAsksNoMoreForAThousandPathsThanForTwo()
    {
        Assert.Equal(await RoutedQueries(2), await RoutedQueries(1000));
    }

    [Fact]
    public async Task AReadingFromTheDiskAsksTheDriveOnce()
    {
        var (tuner, inventory) = OnOneDrive(_temp.Path);
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), tuning: tuner);

        await scanner.MeasureFromDiskAsync(Directories(1000)).AsTask().WaitAsync(Bound);

        Assert.Equal(1, inventory.SubstituteQueries);
    }

    /// <summary>
    /// With a build of the drive's table held in flight, the walk raced against it answers first, and
    /// is registered with the build. The build asks about its own volume as it starts, which is per
    /// build, so the count is compared with a measurement of two paths rather than fixed.
    /// </summary>
    [Fact]
    public async Task AMeasurementRacingABuildAsksNoMoreForAThousandPathsThanForTwo()
    {
        Assert.Equal(await RacedQueries(2), await RacedQueries(1000));
    }

    /// <summary>
    /// A letter <c>subst</c> made is still followed to the folder it stands for, and each of the two
    /// roots on the way is asked about once for the whole measurement.
    /// </summary>
    [Fact]
    public void ASubstitutedLetterIsFollowedAndEachRootAskedOnce()
    {
        var queries = new FakeStorageQueries().Volume(@"C:\", 0).Disk(0, Sata, seekPenalty: true);
        var inventory = new FakeVolumeInventory().With(@"C:\").Substituting(@"S:\", @"C:\Users\testuser");
        var tuner = Tuner(queries, inventory);
        var storages = tuner.ForOneMeasurement();

        var placed = Enumerable.Range(0, 100).Select(i => storages.PlaceOf($@"S:\cache{i}")).ToList();

        Assert.All(placed, storage => Assert.Equal(@"C:\", storage?.VolumeRoot));
        Assert.Equal(2, inventory.SubstituteQueries);
    }

    /// <summary>
    /// A path under a folder another disk's volume is mounted at is walked in that disk's pipeline,
    /// with that disk's values, and its sibling on the drive in the drive's own.
    /// </summary>
    [Fact]
    public void APathUnderAMountPointToAnotherDiskIsPlannedOnThatDisk()
    {
        var queries = new FakeStorageQueries()
            .Volume(@"C:\", 0).Disk(0, Sata, seekPenalty: true)
            .Volume(@"C:\Mount\", 1).Disk(1, Nvme, seekPenalty: false);
        var inventory = new FakeVolumeInventory().With(@"C:\").With(@"C:\Mount\");

        // Auto walks every kind with the same values, so the spinning disk is given its own.
        var tuner = Tuner(
            queries,
            inventory,
            AppPreferences.Default with { Scanning = ScanPreferences.Default with { Rotational = new MediaScanPreferences(WalkThreads: 2) } });
        ScanRequest[] asked = [new(@"C:\cache"), new(@"C:\Mount\cache"), new(@"C:\other")];

        var sets = DirectoryWalks.Plan(asked, [0, 1, 2], tuner, tuner.ForOneMeasurement());

        Assert.Equal(2, sets.Count);
        var onDrive = Assert.Single(sets[0]);
        var onMount = Assert.Single(sets[1]);
        Assert.Equal([0, 2], onDrive.Indexes);
        Assert.Equal([1], onMount.Indexes);
        Assert.Equal(tuner.For(@"C:\cache").Walk, onDrive.Tuning);
        Assert.Equal(tuner.For(@"C:\Mount\cache").Walk, onMount.Tuning);
        Assert.NotEqual(onDrive.Tuning, onMount.Tuning);
    }

    /// <summary>
    /// A walk with no build in flight has no priority to change, so it asks nothing about its paths. A
    /// build that starts during it asks then, outside every lock, and still yields to it.
    /// </summary>
    [Fact]
    public void AWalkAsksAboutItsPathsOnlyOnceABuildCouldYieldToIt()
    {
        var (path, sources) = HeldTree();
        var build = char.ToUpperInvariant(path[0]);
        var other = build is 'Q' ? 'R' : 'Q';
        var (tuner, inventory) = OnOneDisk(build, other);
        var cache = new MftVolumeIndexCache(sources, tuner);
        using var hold = cache.Hold();

        try
        {
            using var walking = cache.Walking([$@"{other}:\a", $@"{other}:\b"]);
            Assert.Equal(0, inventory.SubstituteQueries);

            cache.Get(build);

            // Low only through the walk's own disk, which the build had to ask about to yield to it.
            Assert.Equal([ReadPriority.Low], sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    [Fact]
    public async Task AWalkAfterTheBuildHasEndedAsksNothing()
    {
        var (path, sources) = HeldTree();
        var build = char.ToUpperInvariant(path[0]);
        var other = build is 'Q' ? 'R' : 'Q';
        var (tuner, inventory) = OnOneDisk(build, other);
        var cache = new MftVolumeIndexCache(sources, tuner);
        using var hold = cache.Hold();

        var building = cache.Get(build);
        sources.Release();
        await building.WaitAsync(Bound);
        var before = inventory.SubstituteQueries;

        using var walking = cache.Walking([$@"{other}:\a", $@"{other}:\b"]);

        Assert.Equal(before, inventory.SubstituteQueries);
    }

    private static async Task<int> RoutedQueries(int paths)
    {
        using var temp = new TempDirectory();
        var (tuner, inventory) = OnOneDrive(temp.Path);
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), tuning: tuner);
        using var hold = scanner.Hold();

        await scanner.MeasureManyAsync([.. Enumerable.Range(0, paths).Select(i => new ScanRequest(temp.CreateDirectory($"d{i}")))])
            .AsTask()
            .WaitAsync(Bound);

        return inventory.SubstituteQueries;
    }

    private static async Task<int> RacedQueries(int paths)
    {
        using var temp = new TempDirectory();
        var (path, fixture) = MirroredTree.Realise(temp, new TreeDirectory("cache", new TreeFile("a.bin", 4096)));
        var sources = new HeldMftSourceFactory(char.ToUpperInvariant(Path.GetFullPath(path)[0]), fixture);
        var (tuner, inventory) = OnOneDrive(path);
        var scanner = new DirectoryScanner(sources, tuning: tuner);
        using var hold = scanner.Hold();

        try
        {
            ScanRequest[] asked = [new(path), .. Enumerable.Range(1, paths - 1).Select(i => new ScanRequest(temp.CreateDirectory($"d{i}")))];

            var results = await scanner.MeasureManyAsync(asked).AsTask().WaitAsync(Bound);

            // The race ran: the build was opened and held, and the walk answered every path first.
            Assert.Equal(1, sources.OpenCount);
            Assert.All(results, result => Assert.Equal(FallbackReason.WalkAnsweredFirst, result.Fallback));

            return inventory.SubstituteQueries;
        }
        finally
        {
            sources.Release();
        }
    }

    private ScanRequest[] Directories(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new ScanRequest(_temp.CreateDirectory($"d{i}")))];

    /// <summary>A machine whose one volume, the drive holding <paramref name="path"/>, is on an NVMe disk.</summary>
    private static (ScanTuner Tuner, FakeVolumeInventory Inventory) OnOneDrive(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        var inventory = new FakeVolumeInventory().With(root);

        return (Tuner(new FakeStorageQueries().Volume(root, 0).Disk(0, Nvme, seekPenalty: false), inventory), inventory);
    }

    /// <summary>A machine whose volumes at <paramref name="letters"/> are all on one NVMe disk.</summary>
    private static (ScanTuner Tuner, FakeVolumeInventory Inventory) OnOneDisk(params char[] letters)
    {
        var queries = new FakeStorageQueries().Disk(0, Nvme, seekPenalty: false);
        var inventory = new FakeVolumeInventory();

        foreach (var letter in letters)
        {
            queries.Volume($@"{letter}:\", 0);
            inventory.With($@"{letter}:\");
        }

        return (Tuner(queries, inventory), inventory);
    }

    private static ScanTuner Tuner(FakeStorageQueries queries, FakeVolumeInventory inventory, AppPreferences? preferences = null) =>
        new(new FakePreferences(preferences ?? AppPreferences.Default), new VolumeMediaCache(queries), inventory);

    private (string Path, HeldMftSourceFactory Sources) HeldTree()
    {
        var (path, fixture) = MirroredTree.Realise(_temp, new TreeDirectory("cache", new TreeFile("a.bin", 4096)));

        return (path, new HeldMftSourceFactory(char.ToUpperInvariant(Path.GetFullPath(path)[0]), fixture));
    }
}
