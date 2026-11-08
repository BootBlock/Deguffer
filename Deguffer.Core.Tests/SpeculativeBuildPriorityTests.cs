using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Under Auto a volume's index build reads its table at low priority while a walk is in flight on a
/// disk its volume is on, at normal priority otherwise, and at normal priority from the moment a caller
/// waits for it alone. File table first never lowers it, and Explore opens its own volume without the
/// cache, so never does either (<see cref="BuildPriorities"/>, #478).
///
/// <para>The builds are held with <see cref="HeldMftSourceFactory"/>, whose table is not read until
/// the test says, so a build is still running whenever a test asks what priority it reads at.</para>
/// </summary>
public sealed class SpeculativeBuildPriorityTests : IDisposable
{
    private const byte Nvme = 0x11;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ABuildWithNoWalkInFlightReadsAtNormalPriority()
    {
        var (path, sources) = HeldTree();
        var cache = new MftVolumeIndexCache(sources, RouteTuners.Auto);
        using var hold = cache.Hold();

        try
        {
            Assert.False(cache.Get(path[0]).IsCompleted);
            Assert.Empty(sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>Lowered when a walk on its disk starts, raised when the walk ends.</summary>
    [Fact]
    public void ABuildYieldsWhileAWalkOnItsDiskIsInFlight()
    {
        var (path, sources) = HeldTree();
        var cache = new MftVolumeIndexCache(sources, RouteTuners.Auto);
        using var hold = cache.Hold();

        try
        {
            cache.Get(path[0]);

            using (cache.Walking([path]))
            {
                Assert.Equal([ReadPriority.Low], sources.Opened!.Priorities);
            }

            Assert.Equal([ReadPriority.Low, ReadPriority.Normal], sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    [Fact]
    public void ABuildStartedDuringAWalkOnItsDiskStartsLow()
    {
        var (path, sources) = HeldTree();
        var cache = new MftVolumeIndexCache(sources, RouteTuners.Auto);
        using var hold = cache.Hold();

        try
        {
            using var walking = cache.Walking([path]);
            cache.Get(path[0]);

            Assert.Equal([ReadPriority.Low], sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A walk of a volume on another disk shares nothing a low priority could protect, and the build
    /// would only answer later. A volume on the build's disk is walked through the same disk.
    /// </summary>
    [Fact]
    public void OnlyAWalkOnTheBuildsDiskLowersIt()
    {
        var (path, sources) = HeldTree();
        var build = char.ToUpperInvariant(path[0]);
        var (sameDisk, otherDisk) = OtherLetters(build);
        var cache = new MftVolumeIndexCache(sources, OnDisks((build, 0), (sameDisk, 0), (otherDisk, 1)));
        using var hold = cache.Hold();

        try
        {
            cache.Get(build);

            using (cache.Walking([$@"{otherDisk}:\"]))
            {
                Assert.Empty(sources.Opened!.Priorities);
            }

            using (cache.Walking([$@"{sameDisk}:\"]))
            {
                Assert.Equal([ReadPriority.Low], sources.Opened!.Priorities);
            }
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A volume that did not say which disks hold it may share the build's, and the walk is the answer
    /// being waited for, so the build yields to it.
    /// </summary>
    [Fact]
    public void AWalkOfAVolumeWhoseDisksAreUnknownLowersTheBuild()
    {
        var (path, sources) = HeldTree();
        var build = char.ToUpperInvariant(path[0]);
        var (unnamed, _) = OtherLetters(build);
        var cache = new MftVolumeIndexCache(sources, OnDisks((build, 0)));
        using var hold = cache.Hold();

        try
        {
            cache.Get(build);
            using var walking = cache.Walking([$@"{unnamed}:\"]);

            Assert.Equal([ReadPriority.Low], sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A mapped network drive, and a share, are on no disk of this machine, which their media says
    /// without a reason. A walk of either shares nothing with a build, however long it takes.
    /// </summary>
    [Fact]
    public void AWalkOfANetworkDriveOrAShareLowersNothing()
    {
        var (path, sources) = HeldTree();
        var build = char.ToUpperInvariant(path[0]);
        var (mapped, _) = OtherLetters(build);
        var queries = new FakeStorageQueries().Volume($@"{build}:\", 0).Disk(0, Nvme, seekPenalty: false);
        var inventory = new FakeVolumeInventory().With($@"{build}:\").With($@"{mapped}:\", DriveType.Network);
        var cache = new MftVolumeIndexCache(
            sources, new ScanTuner(new FakePreferences(AppPreferences.Default), new VolumeMediaCache(queries), inventory));
        using var hold = cache.Hold();

        try
        {
            cache.Get(build);
            using var walking = cache.Walking([$@"{mapped}:\projects", @"\\server.test\share\projects"]);

            Assert.Empty(sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A folder mounted from a volume on another disk is walked through that disk, though its path
    /// starts with the build's drive letter.
    /// </summary>
    [Fact]
    public void AWalkUnderAFolderMountedFromAnotherDiskLowersNothing()
    {
        var (path, sources) = HeldTree();
        var build = char.ToUpperInvariant(path[0]);
        var mount = $@"{build}:\mounted\";
        var queries = new FakeStorageQueries()
            .Volume($@"{build}:\", 0).Disk(0, Nvme, seekPenalty: false)
            .Volume(mount, 1).Disk(1, Nvme, seekPenalty: false);
        var inventory = new FakeVolumeInventory().With($@"{build}:\").With(mount);
        var cache = new MftVolumeIndexCache(
            sources, new ScanTuner(new FakePreferences(AppPreferences.Default), new VolumeMediaCache(queries), inventory));
        using var hold = cache.Hold();

        try
        {
            cache.Get(build);

            using (cache.Walking([mount + "projects"]))
            {
                Assert.Empty(sources.Opened!.Priorities);
            }

            using (cache.Walking([$@"{build}:\elsewhere"]))
            {
                Assert.Equal([ReadPriority.Low], sources.Opened!.Priorities);
            }
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>File table first waits for every answer the table gives, so its build is never speculative.</summary>
    [Fact]
    public async Task FileTableFirstNeverLowersTheBuild()
    {
        var (path, sources) = HeldTree();
        var scanner = new DirectoryScanner(sources, tuning: RouteTuners.Table);
        using var walking = scanner.Walking(path);

        var measuring = scanner.MeasureAsync(path).AsTask();
        sources.Release();
        var result = await measuring.WaitAsync(Bound);

        Assert.Equal(ScanStrategy.MasterFileTable, result.Strategy);
        Assert.Empty(sources.Opened!.Priorities);
    }

    /// <summary>A build a caller needs is raised once, whatever case the letter is asked in, and stays raised.</summary>
    [Fact]
    public void ABuildACallerNeedsIsRaisedForGood()
    {
        var (path, sources) = HeldTree();
        var cache = new MftVolumeIndexCache(sources, RouteTuners.Auto);
        using var hold = cache.Hold();

        try
        {
            var first = cache.Walking([path]);
            cache.Get(char.ToUpperInvariant(path[0]));
            cache.Need(char.ToLowerInvariant(path[0]));
            cache.Need(path[0]);
            first.Dispose();
            using var second = cache.Walking([path]);

            Assert.Equal([ReadPriority.Low, ReadPriority.Normal], sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A caller that will wait alone can say so before the question that starts the build, and the
    /// build then never yields.
    /// </summary>
    [Fact]
    public void ABuildNeededBeforeItStartsNeverYields()
    {
        var (path, sources) = HeldTree();
        var cache = new MftVolumeIndexCache(sources, RouteTuners.Auto);
        using var hold = cache.Hold();

        try
        {
            using var walking = cache.Walking([path]);
            cache.Need(path[0]);
            cache.Get(path[0]);

            Assert.Empty(sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A caller can need a build while another thread is still opening its volume. The open must not
    /// lower it afterwards, because that caller is already waiting.
    /// </summary>
    [Fact]
    public void ABuildNeededWhileItsVolumeOpensIsNeverLowered()
    {
        var (path, held) = HeldTree();
        var sources = new NeededWhileOpening(held);
        var cache = new MftVolumeIndexCache(sources, RouteTuners.Auto);
        sources.Cache = cache;
        using var hold = cache.Hold();

        try
        {
            using var walking = cache.Walking([path]);
            cache.Get(path[0]);

            Assert.Empty(held.Opened!.Priorities);
        }
        finally
        {
            held.Release();
        }
    }

    [Fact]
    public void NeedingAVolumeNothingHasStartedOpensNothing()
    {
        var (path, sources) = HeldTree();
        var cache = new MftVolumeIndexCache(sources, RouteTuners.Auto);

        cache.Need(path[0]);

        Assert.Equal(0, sources.OpenCount);
    }

    /// <summary>A build the next scan dropped is ending, and a walk after it asks nothing of its volume.</summary>
    [Fact]
    public void ABuildTheScanDroppedIsNotLoweredAgain()
    {
        var (path, sources) = HeldTree();
        var cache = new MftVolumeIndexCache(sources, RouteTuners.Auto);
        using var hold = cache.Hold();

        try
        {
            cache.Get(path[0]);
            var dropped = sources.Opened!;
            cache.Invalidate();

            using var walking = cache.Walking([path]);

            Assert.Empty(dropped.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A walk that answers first leaves the build reading for the questions after it, at normal priority
    /// once nothing walks its disk.
    /// </summary>
    [Fact]
    public async Task TheRacedWalkLowersTheBuildUntilItEnds()
    {
        var (path, sources) = HeldTree();
        var scanner = new DirectoryScanner(sources, tuning: RouteTuners.Auto);
        using var hold = scanner.Hold();

        try
        {
            var result = await scanner.MeasureAsync(path).AsTask().WaitAsync(Bound);

            Assert.Equal(ScanStrategy.ParallelEnumeration, result.Strategy);
            Assert.Equal([ReadPriority.Low, ReadPriority.Normal], sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A walk that cannot reach its path leaves the table as the only route answering, so the
    /// measurement raises the build before it waits, though another walk on its disk is still in flight.
    /// </summary>
    [Fact]
    public async Task AMeasurementWhoseWalkCannotReachItsPathRaisesTheBuild()
    {
        var (path, sources) = HeldTree();
        using var denied = new DeniedDirectory(path);
        var scanner = new DirectoryScanner(sources, tuning: RouteTuners.Auto);
        using var other = scanner.Walking(_temp.CreateDirectory("other"));

        var measuring = scanner.MeasureAsync(path).AsTask();

        try
        {
            await WaitUntil(() => sources.Opened?.Priorities.Count == 2);
            Assert.False(measuring.IsCompleted);
            Assert.Equal([ReadPriority.Low, ReadPriority.Normal], sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }

        Assert.Equal(ScanStrategy.MasterFileTable, (await measuring.WaitAsync(Bound)).Strategy);
    }

    /// <summary>The same for a search of a source folder whose root the walk cannot list.</summary>
    [Fact]
    public async Task ASearchWhoseWalkCannotListItsRootRaisesTheBuild()
    {
        var (root, fixture) = MirroredTree.Realise(_temp, new TreeDirectory(
            "src",
            new TreeDirectory("Example", new TreeDirectory("obj", new TreeFile("a.dll", 10)))));
        using var denied = new DeniedDirectory(root);
        var sources = Held(root, fixture);
        var scanner = new DirectoryScanner(sources, tuning: RouteTuners.Auto);
        using var other = scanner.Walking(_temp.CreateDirectory("other"));
        var discovery = new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory());
        discovery.Include(["obj"]);

        var finding = discovery.FindAsync([new SourceRoot(root)]);

        try
        {
            await WaitUntil(() => sources.Opened?.Priorities.Count == 2);
            Assert.False(finding.IsCompleted);
            Assert.Equal([ReadPriority.Low, ReadPriority.Normal], sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }

        Assert.Equal([Path.Combine(root, "Example", "obj")], (await finding.WaitAsync(Bound)).Candidates);
    }

    /// <summary>
    /// A build started under Auto that a later question waits for alone, because the route changed
    /// between the two, is raised for the wait.
    /// </summary>
    [Fact]
    public async Task AWaitAfterTheRouteChangesToFileTableFirstRaisesTheBuild()
    {
        var (path, sources) = HeldTree();
        var preferences = new FakePreferences(AppPreferences.Default);
        var scanner = new DirectoryScanner(
            sources,
            tuning: new ScanTuner(preferences, new VolumeMediaCache(new FakeStorageQueries()), new FakeVolumeInventory()));

        // Held for both questions, as a planning pass holds it, so the second waits on the first's build.
        using var hold = scanner.Hold();
        using var other = scanner.Walking(_temp.CreateDirectory("other"));

        try
        {
            await scanner.MeasureAsync(path).AsTask().WaitAsync(Bound);
            Assert.Equal([ReadPriority.Low], sources.Opened!.Priorities);
            preferences.Current = AppPreferences.Default with { Scanning = ScanPreferences.Default with { Route = ScanRoute.Table } };

            var measuring = scanner.MeasureManyAsync([new ScanRequest(path)]).AsTask();

            await WaitUntil(() => sources.Opened?.Priorities.Count == 2);
            Assert.Equal([ReadPriority.Low, ReadPriority.Normal], sources.Opened!.Priorities);

            sources.Release();
            Assert.Equal(ScanStrategy.MasterFileTable, (await measuring.WaitAsync(Bound))[0].Strategy);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A path the table cannot answer for is walked after the race, and that walk is an answer being
    /// waited for too, so a build on its disk yields to it.
    /// </summary>
    [Fact]
    public async Task AWalkOfAPathTheTableCannotAnswerLowersTheBuild()
    {
        var (path, sources) = HeldTree();
        var scanner = new DirectoryScanner(sources, tuning: RouteTuners.Auto);
        using var hold = scanner.Hold();

        try
        {
            await scanner.MeasureAsync(path).AsTask().WaitAsync(Bound);
            await scanner.MeasureAsync(AbsentElsewhere(path)).AsTask().WaitAsync(Bound);

            Assert.Equal([ReadPriority.Low, ReadPriority.Normal, ReadPriority.Low, ReadPriority.Normal], sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>A reading taken from the disk on purpose walks, and lowers a build on its disk the same way.</summary>
    [Fact]
    public async Task AReadingFromTheDiskLowersTheBuild()
    {
        var (path, sources) = HeldTree();
        var scanner = new DirectoryScanner(sources, tuning: RouteTuners.Auto);
        using var hold = scanner.Hold();

        try
        {
            await scanner.MeasureAsync(path).AsTask().WaitAsync(Bound);
            await scanner.MeasureFromDiskAsync([new ScanRequest(AbsentElsewhere(path))]).AsTask().WaitAsync(Bound);

            Assert.Equal([ReadPriority.Low, ReadPriority.Normal, ReadPriority.Low, ReadPriority.Normal], sources.Opened!.Priorities);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A search that does not race says it waits alone before it asks, so a build its question starts
    /// does not yield to walks the search is not making, and says when it walks the root itself.
    /// </summary>
    [Fact]
    public async Task ASearchThatDoesNotRaceNeedsTheIndexBeforeItAsksAndSaysWhenItWalks()
    {
        var root = _temp.CreateDirectory("src");
        var scanner = new OrderRecordingScanner(new FakeDirectoryScanner());
        var discovery = new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory());
        discovery.Include(["obj"]);

        await discovery.FindAsync([new SourceRoot(root)]).WaitAsync(Bound);

        Assert.Equal(["need " + root, "find " + root, "walk " + root, "walked " + root], scanner.Calls);
    }

    private (string Path, HeldMftSourceFactory Sources) HeldTree()
    {
        var (path, fixture) = MirroredTree.Realise(_temp, new TreeDirectory("cache", new TreeFile("a.bin", 4096)));

        return (path, Held(path, fixture));
    }

    private static HeldMftSourceFactory Held(string path, MftFixture fixture) =>
        new(char.ToUpperInvariant(Path.GetFullPath(path)[0]), fixture);

    /// <summary>Two drive letters that are not <paramref name="taken"/>, named by nothing but the test.</summary>
    private static (char First, char Second) OtherLetters(char taken) => taken is 'Q' or 'R' ? ('S', 'T') : ('Q', 'R');

    /// <summary>A folder on a drive letter other than <paramref name="path"/>'s, which does not exist.</summary>
    private static string AbsentElsewhere(string path) => $@"{OtherLetters(char.ToUpperInvariant(path[0])).First}:\deguffer-absent";

    /// <summary>Auto, on a machine whose volumes sit on the NVMe disks given.</summary>
    private static ScanTuner OnDisks(params (char Letter, int Disk)[] volumes)
    {
        var queries = new FakeStorageQueries();
        var inventory = new FakeVolumeInventory();

        foreach (var (letter, disk) in volumes)
        {
            queries.Volume($@"{letter}:\", disk).Disk(disk, Nvme, seekPenalty: false);
            inventory.With($@"{letter}:\");
        }

        return new ScanTuner(new FakePreferences(AppPreferences.Default), new VolumeMediaCache(queries), inventory);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Bound;

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition never held.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A scanner that does not race, noting in order each search asked of it, each index needed, and
    /// each walk said to start and end.
    /// </summary>
    private sealed class OrderRecordingScanner(FakeDirectoryScanner inner) : IDirectoryScanner
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _calls = new();

        public IReadOnlyList<string> Calls => [.. _calls];

        public ScanTuner Tuning => inner.Tuning;

        public ValueTask<ScanResult> MeasureAsync(
            string path,
            MinimumAge keep = default,
            ScanBounds bounds = default,
            IProgress<ScanSize>? progress = null,
            CancellationToken ct = default) =>
            inner.MeasureAsync(path, keep, bounds, progress, ct);

        public ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
            IReadOnlyList<ScanRequest> asked,
            MinimumAge keep = default,
            CancellationToken ct = default) =>
            inner.MeasureManyAsync(asked, keep, ct);

        public ValueTask<AgedScan> MeasureAgedAsync(string path, MinimumAge age, CancellationToken ct = default) =>
            inner.MeasureAgedAsync(path, age, ct);

        public ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default) =>
            inner.MeasureFromDiskAsync(asked, ct);

        public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
            DirectorySearch search,
            string root,
            CancellationToken ct = default)
        {
            _calls.Enqueue("find " + root);
            return inner.TryFindDirectoriesAsync(search, root, ct);
        }

        public void NeedIndex(string root) => _calls.Enqueue("need " + root);

        public IDisposable? Walking(string root)
        {
            _calls.Enqueue("walk " + root);
            return new PassHold(() => _calls.Enqueue("walked " + root));
        }

        public void Invalidate() => inner.Invalidate();

        public PassHold Hold() => inner.Hold();
    }

    /// <summary>A factory whose open of the volume is interrupted by a caller needing the build.</summary>
    private sealed class NeededWhileOpening(HeldMftSourceFactory inner) : IMftSourceFactory
    {
        public MftVolumeIndexCache? Cache { get; set; }

        public IMftVolume? TryOpen(char driveLetter, out FallbackReason reason, MftOpenTimings? timings = null)
        {
            Cache!.Need(driveLetter);

            return inner.TryOpen(driveLetter, out reason, timings);
        }
    }
}
