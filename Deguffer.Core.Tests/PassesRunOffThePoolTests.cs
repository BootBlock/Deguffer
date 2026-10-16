using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A pass over a file table, or a walk, holds the thread that drives it for its whole length, so none
/// is driven from the thread pool: a preview building the index of more volumes than the machine has
/// processors, or measuring for more providers at once, would hold the pool's every thread, and each
/// pass past its minimum would wait for it to grow.
///
/// <para>Each test watches the crews its operation makes through <see cref="WorkerCrew.Made"/>, which
/// is told on the thread that drives each one. <see cref="WorkerThreadsTests"/> proves that the
/// threads these passes run on start at once however many run together. These pin that each pass is
/// handed to them.</para>
/// </summary>
public sealed class PassesRunOffThePoolTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Measurements => ["one", "aged", "many"];

    private static MftFixture Volume() => new MftFixture()
        .AddDirectory(6, MftRecord.RootRecordNumber, "Users")
        .AddFile(20, 6, "a.bin", allocated: 4096, logical: 4000);

    [Fact]
    public async Task AVolumesIndexIsBuiltOffThePool()
    {
        var letter = DriveLetters.Unused();
        var cache = new MftVolumeIndexCache(FakeMftSourceFactory.Serving(letter, Volume()), ScanTuner.Shipped);
        MftVolumeIndexCache.Built built = default;

        CrewThreads.AssertOffThePool(await CrewThreads.MadeBy(async () => built = await cache.Get(letter)));
        Assert.NotNull(built.Index);
    }

    /// <summary>
    /// A build the system would not start a thread for closes the volume it opened: only the build
    /// closes it, and a handle left open would hold the volume for the life of the cache.
    /// </summary>
    [Fact]
    public async Task ABuildWithNoThreadClosesTheVolumeItOpened()
    {
        var letter = DriveLetters.Unused();
        var sources = new HeldMftSourceFactory(letter, Volume());

        // Its reads go ahead, so a build that started after all ends, and the test fails rather than waits.
        sources.Release();
        var cache = new MftVolumeIndexCache(sources, ScanTuner.Shipped, static _ => throw new OutOfMemoryException("no thread"));

        await Assert.ThrowsAsync<OutOfMemoryException>(() => cache.Get(letter));

        Assert.Equal(1, sources.OpenCount);
        Assert.Equal(1, sources.CloseCount);
    }

    [Fact]
    public async Task AnExploreScanReadsItsTableOffThePool()
    {
        var letter = DriveLetters.Unused();
        var scanner = new ExploreScanner(FakeMftSourceFactory.Serving(letter, Volume()));
        ExploreScan? scan = null;

        CrewThreads.AssertOffThePool(await CrewThreads.MadeBy(async () => scan = await scanner.ScanAsync($@"{letter}:\")));
        Assert.Equal(ScanStrategy.MasterFileTable, scan!.Strategy);
    }

    [Fact]
    public async Task AFolderScanReadsItsTableOffThePool()
    {
        var letter = DriveLetters.Unused();
        var scanner = new ExploreScanner(FakeMftSourceFactory.Serving(letter, Volume()));
        IReadOnlyList<ScannedFolder> scans = [];

        CrewThreads.AssertOffThePool(await CrewThreads.MadeBy(async () => scans = await scanner.ScanFoldersAsync([$@"{letter}:\Users"])));
        Assert.Equal(ScanStrategy.MasterFileTable, Assert.Single(scans).Strategy);
    }

    [Theory]
    [MemberData(nameof(Measurements))]
    public async Task TheWalkMeasuresOffThePool(string measurement)
    {
        var folder = Tree();
        var scanner = new ParallelEnumerationScanner(ScanTuner.Shipped);

        CrewThreads.AssertOffThePool(await CrewThreads.MadeBy(() => Measure(scanner, measurement, folder)));
    }

    [Theory]
    [MemberData(nameof(Measurements))]
    public async Task TheHardLinkAwareWalkMeasuresOffThePool(string measurement)
    {
        var folder = Tree();
        var scanner = new HardLinkAwareScanner(ScanTuner.Shipped);

        CrewThreads.AssertOffThePool(await CrewThreads.MadeBy(() => Measure(scanner, measurement, folder)));
    }

    /// <summary>The look inside a tree for an Outlook data file before a command is let loose on it.</summary>
    [Fact]
    public async Task AWholeTreeLookWalksOffThePool()
    {
        var folder = Tree();

        CrewThreads.AssertOffThePool(await CrewThreads.MadeBy(() => WholeTreeLook.TakeAsync([folder], MinimumAge.Off, default)));
    }

    /// <summary>The walk a removal makes of a folder before it takes anything from it.</summary>
    [Fact]
    public async Task ADirectoryIsWalkedForItsRemovalOffThePool()
    {
        var folder = Tree();
        var sibling = _temp.CreateFile(16, "profile", "kept.txt");
        RemovalOutcome? outcome = null;

        CrewThreads.AssertOffThePool(await CrewThreads.MadeBy(async () => outcome = await DirectoryRemover.RemoveAsync(folder)));
        Assert.True(outcome!.RootRemoved);
        Assert.True(LongPath.FileExists(sibling));
    }

    /// <summary>The look inside a folder for an Outlook data file, before the Recycle Bin is asked to take it.</summary>
    [Fact]
    public async Task AFolderIsLookedInsideOffThePoolBeforeTheRecycleBinTakesIt()
    {
        var folder = Tree();
        var policy = new ExploreActionPolicy(
            ProtectedRegions.For(new FakeSystemDirectories(_temp.Path), new FakeUserEnvironment(_temp.Path)),
            [],
            new FakeVolumeInventory());
        var bin = new FakeRecycleBin();

        CrewThreads.AssertOffThePool(await CrewThreads.MadeBy(() => ExploreRemover.RemoveAsync(
            [new ExploreItem(folder, IsDirectory: true, Bytes: 96)],
            ExploreRemovalMode.RecycleBin,
            policy,
            bin)));
        Assert.Equal(folder, Assert.Single(bin.Paths));
    }

    private static Task Measure(IDirectoryScanner scanner, string measurement, string folder) => measurement switch
    {
        "one" => scanner.MeasureAsync(folder).AsTask(),
        "aged" => scanner.MeasureAgedAsync(folder, MinimumAge.Off).AsTask(),
        _ => scanner.MeasureManyAsync([new ScanRequest(folder)]).AsTask(),
    };

    private string Tree()
    {
        var folder = _temp.CreateDirectory("profile", "cache");
        _temp.CreateFile(64, "profile", "cache", "a.bin");
        _temp.CreateFile(32, "profile", "cache", "nested", "b.bin");
        return folder;
    }
}
