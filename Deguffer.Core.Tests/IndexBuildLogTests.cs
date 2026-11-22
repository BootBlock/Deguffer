using Deguffer.Core.Configuration;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A planning pass records, for each volume, when its index build opened the volume and was ready,
/// how long it read at low priority, and which route answered each question about it, so the cost of
/// a build yielding to the walks it races is measured (#1081). Recorded only in the flow that opened
/// the log.
/// </summary>
public sealed class IndexBuildLogTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _clock = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>Each span at low priority counts, a span at normal priority between them does not, and the build's end closes the last.</summary>
    [Fact]
    public void ATraceSumsTheSpansAtLowPriorityUpToItsEnd()
    {
        var log = new IndexBuildLog(_clock);
        var trace = log.Of('c');

        _clock.Advance(TimeSpan.FromSeconds(1));
        trace.Started();
        _clock.Advance(TimeSpan.FromSeconds(1));
        trace.Prioritised(ReadPriority.Low);
        _clock.Advance(TimeSpan.FromSeconds(3));
        trace.Prioritised(ReadPriority.Normal);
        _clock.Advance(TimeSpan.FromSeconds(1));
        trace.Prioritised(ReadPriority.Low);
        _clock.Advance(TimeSpan.FromSeconds(2));
        trace.Ended(ready: true);
        _clock.Advance(TimeSpan.FromSeconds(10));

        var times = Assert.Single(log.Volumes);
        Assert.Equal('C', times.DriveLetter);
        Assert.Equal(TimeSpan.FromSeconds(1), times.Started);
        Assert.Equal(TimeSpan.FromSeconds(8), times.Ready);
        Assert.Equal(TimeSpan.FromSeconds(5), times.AtLowPriority);
    }

    /// <summary>A build that ended without an index was never ready, and one still reading counts its low span up to now.</summary>
    [Fact]
    public void ABuildThatFailedIsNeverReadyAndOneStillReadingCountsUpToNow()
    {
        var log = new IndexBuildLog(_clock);
        var failed = log.Of('D');
        var reading = log.Of('E');

        failed.Started();
        reading.Started();
        reading.Prioritised(ReadPriority.Low);
        _clock.Advance(TimeSpan.FromSeconds(2));
        failed.Ended(ready: false);
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(
            [new IndexBuildTimes('D', TimeSpan.Zero, null, TimeSpan.Zero, 0, 0), new IndexBuildTimes('E', TimeSpan.Zero, null, TimeSpan.FromSeconds(4), 0, 0)],
            log.Volumes);
    }

    /// <summary>An answer counts only in the flow the log records, and the flow goes back to the log that recorded it before.</summary>
    [Fact]
    public void AnAnswerCountsOnlyInTheRecordedFlow()
    {
        var outer = new IndexBuildLog(_clock);
        var inner = new IndexBuildLog(_clock);

        IndexBuildLog.Answered('c', ScanStrategy.MasterFileTable);

        using (outer.RecordIn())
        {
            using (inner.RecordIn())
            {
                IndexBuildLog.Answered('c', ScanStrategy.ParallelEnumeration);
            }

            IndexBuildLog.Answered('c', ScanStrategy.MasterFileTable);
        }

        IndexBuildLog.Answered('c', ScanStrategy.MasterFileTable);

        Assert.Equal((1, 0), Answers(outer, 'C'));
        Assert.Equal((0, 1), Answers(inner, 'C'));
    }

    /// <summary>
    /// A build records its start, its low span while a walk on its disk is in flight, and when it was
    /// ready, though it reads on a thread of its own and the walk ends on the test's.
    /// </summary>
    [Fact]
    public async Task ABuildRecordsItsStartItsLowSpanAndWhenItWasReady()
    {
        var (path, sources) = HeldTree();
        var cache = new MftVolumeIndexCache(sources, RouteTuners.Auto);
        var log = new IndexBuildLog(_clock);
        using var hold = cache.Hold();
        Task<MftVolumeIndexCache.Built> building;

        using (log.RecordIn())
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            building = cache.Get(path[0]);

            using (cache.Walking([path]))
            {
                _clock.Advance(TimeSpan.FromSeconds(3));
            }

            _clock.Advance(TimeSpan.FromSeconds(2));
            sources.Release();
            Assert.NotNull((await building.WaitAsync(Bound)).Index);
        }

        var times = Assert.Single(log.Volumes);
        Assert.Equal(char.ToUpperInvariant(path[0]), times.DriveLetter);
        Assert.Equal(TimeSpan.FromSeconds(1), times.Started);
        Assert.Equal(TimeSpan.FromSeconds(6), times.Ready);
        Assert.Equal(TimeSpan.FromSeconds(3), times.AtLowPriority);
    }

    /// <summary>
    /// The walk answers the question it raced a held build for, and the table the one asked under file
    /// table first, which waits for the build rather than racing it again.
    /// </summary>
    [Fact]
    public async Task AMeasurementRecordsWhichRouteAnswered()
    {
        var (path, sources) = HeldTree();
        var preferences = new FakePreferences(AppPreferences.Default);
        var scanner = new DirectoryScanner(
            sources,
            tuning: new ScanTuner(preferences, new VolumeMediaCache(new FakeStorageQueries()), new FakeVolumeInventory()));
        var log = new IndexBuildLog(_clock);
        using var hold = scanner.Hold();

        using (log.RecordIn())
        {
            try
            {
                Assert.Equal(ScanStrategy.ParallelEnumeration, (await scanner.MeasureAsync(path).AsTask().WaitAsync(Bound)).Strategy);
            }
            finally
            {
                sources.Release();
            }

            preferences.Current = AppPreferences.Default with { Scanning = ScanPreferences.Default with { Route = ScanRoute.Table } };
            Assert.Equal(ScanStrategy.MasterFileTable, (await scanner.MeasureAsync(path).AsTask().WaitAsync(Bound)).Strategy);
        }

        Assert.Equal((1, 1), Answers(log, char.ToUpperInvariant(path[0])));
    }

    /// <summary>A search of a source folder counts as one question about its volume, answered by the table here.</summary>
    [Fact]
    public async Task ASearchRecordsWhichRouteAnswered()
    {
        var (root, fixture) = MirroredTree.Realise(_temp, new TreeDirectory(
            "src",
            new TreeDirectory("Example", new TreeDirectory("obj", new TreeFile("a.dll", 10)))));
        var sources = new HeldMftSourceFactory(char.ToUpperInvariant(Path.GetFullPath(root)[0]), fixture);
        sources.Release();
        var discovery = new SourceDirectoryDiscovery(new DirectoryScanner(sources, tuning: RouteTuners.Table), new FakeVolumeInventory());
        discovery.Include(["obj"]);
        var log = new IndexBuildLog(_clock);

        using (log.RecordIn())
        {
            Assert.Single((await discovery.FindAsync([new SourceRoot(root)]).WaitAsync(Bound)).Candidates);
        }

        Assert.Equal((1, 0), Answers(log, char.ToUpperInvariant(root[0])));
    }

    private static (int ByTable, int ByWalk) Answers(IndexBuildLog log, char driveLetter)
    {
        var times = Assert.Single(log.Volumes, volume => volume.DriveLetter == driveLetter);

        return (times.TableAnswers, times.WalkAnswers);
    }

    private (string Path, HeldMftSourceFactory Sources) HeldTree()
    {
        var (path, fixture) = MirroredTree.Realise(_temp, new TreeDirectory("cache", new TreeFile("a.bin", 4096)));

        return (path, new HeldMftSourceFactory(char.ToUpperInvariant(Path.GetFullPath(path)[0]), fixture));
    }
}
