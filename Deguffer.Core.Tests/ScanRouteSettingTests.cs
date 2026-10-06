using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The route choice: Auto races the walk against the table wherever the table can be read and takes
/// the first answer, Table waits for the table as every scan did before the routes raced, and walk
/// only walks even where the table is readable, and says why.
///
/// <para>The races are held still with <see cref="HeldMftSourceFactory"/>, whose table is not read
/// until the test says, so which route answers is never left to timing.</para>
/// </summary>
public sealed class ScanRouteSettingTests : IDisposable
{
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Cache = 8;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static MftFixture Volume() => new MftFixture()
        .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(Profile, Users, "testuser")
        .AddDirectory(Cache, Profile, ".npm-cache")
        .AddFile(20, Cache, "a.tgz", allocated: 8192, logical: 8000);

    /// <summary>
    /// The walk of a small folder answers long before the table is read, and Table still waits for the
    /// table. The table is held for long enough that a walk raced against it would already have
    /// answered, so a raced scan answers from the walk here and fails this.
    /// </summary>
    [Fact]
    public async Task TableWaitsForTheTableWhereTheWalkWouldAnswerFirst()
    {
        var (path, fixture) = SmallTree();
        var sources = Held(path, fixture);
        var scanner = new DirectoryScanner(sources, tuning: Tuner(ScanRoute.Table).Tuner);

        var measuring = scanner.MeasureAsync(path).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        sources.Release();
        var result = await measuring;

        Assert.Equal(ScanStrategy.MasterFileTable, result.Strategy);
        Assert.Equal(4096, result.Size.Logical);
    }

    /// <summary>
    /// While the table is still being read, Auto takes the walk's answer and says nothing about it:
    /// nothing was unavailable, and administrator rights are already held. Once the table is read,
    /// it answers.
    /// </summary>
    [Fact]
    public async Task AutoTakesTheWalkWhileTheTableIsStillBeingReadAndTheTableOnceItIsRead()
    {
        var (path, fixture) = SmallTree();
        var sources = Held(path, fixture);
        var scanner = new DirectoryScanner(sources, tuning: Tuner(ScanRoute.Auto).Tuner);

        // Bounded, so a scan that waits for the held table fails here rather than hanging.
        var walked = await scanner.MeasureAsync(path).AsTask().WaitAsync(Bound);

        Assert.Equal(ScanStrategy.ParallelEnumeration, walked.Strategy);
        Assert.Equal(FallbackReason.WalkAnsweredFirst, walked.Fallback);
        Assert.Null(walked.FallbackNote);
        Assert.False(ElevationOffer.ShouldOffer(isElevated: false, walked.Fallback, HiddenSpace.None));
        Assert.Equal(4096, walked.Size.Logical);

        sources.Release();

        // A search by name waits for the table, so once it has answered the table is read.
        Assert.NotNull(await scanner.TryFindDirectoriesNamedAsync("cache", path));
        var indexed = await scanner.MeasureAsync(path);

        Assert.Equal(ScanStrategy.MasterFileTable, indexed.Strategy);
        Assert.Equal(walked.Size.Logical, indexed.Size.Logical);
        Assert.Equal(1, sources.OpenCount);
    }

    /// <summary>
    /// A walk refused the folder has measured nothing, and the table may still answer for it, so
    /// the race waits for the table rather than report a folder it never read.
    /// </summary>
    [Fact]
    public async Task AutoWaitsForTheTableWhereTheWalkCannotReadThePath()
    {
        var (path, fixture) = SmallTree();
        using var denied = new DeniedDirectory(path);
        var sources = Held(path, fixture);
        var scanner = new DirectoryScanner(sources, tuning: Tuner(ScanRoute.Auto).Tuner);

        var measuring = scanner.MeasureAsync(path).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        sources.Release();
        var result = await measuring;

        Assert.Equal(ScanStrategy.MasterFileTable, result.Strategy);
        Assert.True(result.WasReached);
        Assert.Equal(4096, result.Size.Logical);
    }

    /// <summary>
    /// Where the table then cannot be read either, the walk's answer stands, with the reason the
    /// table gave rather than the claim that the walk was merely quicker.
    /// </summary>
    [Fact]
    public async Task AutoSaysWhyTheTableDeclinedWhereTheWalkWaitedForIt()
    {
        var (path, fixture) = SmallTree();
        using var denied = new DeniedDirectory(path);
        var sources = Held(path, fixture.UnreadableFrom(MftRecord.ReservedRecordCount));
        var scanner = new DirectoryScanner(sources, tuning: Tuner(ScanRoute.Auto).Tuner);

        var measuring = scanner.MeasureAsync(path).AsTask();
        sources.Release();
        var result = await measuring;

        Assert.Equal(ScanStrategy.ParallelEnumeration, result.Strategy);
        Assert.Equal(FallbackReason.MasterFileTableIncomplete, result.Fallback);
        Assert.False(result.WasReached);
    }

    /// <summary>
    /// A search of a source folder races the same way, and a walk that answered first is not a
    /// fallback, so the plan does not offer administrator rights the process already has.
    /// </summary>
    [Fact]
    public async Task DiscoveryUnderAutoTakesTheWalkWhileTheTableIsStillBeingRead()
    {
        var (root, fixture) = MirroredTree.Realise(_temp, new TreeDirectory(
            "src",
            new TreeDirectory("Example", new TreeDirectory("obj", new TreeFile("a.dll", 10)))));
        var sources = Held(root, fixture);
        var discovery = new SourceDirectoryDiscovery(
            new DirectoryScanner(sources, tuning: Tuner(ScanRoute.Auto).Tuner), new FakeVolumeInventory());
        discovery.Include(["obj"]);

        var found = await discovery.FindAsync([new SourceRoot(root)]).WaitAsync(Bound);

        Assert.Equal([Path.Combine(root, "Example", "obj")], found.Candidates);
        Assert.False(found.FellBack);
        sources.Release();
    }

    /// <summary>
    /// A new scan ends a build the last one left running, rather than leave it reading a table
    /// nothing will ask about, and the volume is opened again for the new scan.
    /// </summary>
    [Fact]
    public async Task InvalidatingEndsABuildStillRunning()
    {
        var (path, fixture) = SmallTree();
        var sources = Held(path, fixture);
        var scanner = new DirectoryScanner(sources, tuning: Tuner(ScanRoute.Table).Tuner);

        var measuring = scanner.MeasureAsync(path).AsTask();
        scanner.Invalidate();
        sources.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measuring);
        Assert.Equal(1, sources.CloseCount);

        Assert.Equal(ScanStrategy.MasterFileTable, (await scanner.MeasureAsync(path)).Strategy);
        Assert.Equal(2, sources.OpenCount);
    }

    /// <summary>Far longer than a walk of a few entries takes, and short enough to fail a run that waits.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private (string Path, MftFixture Fixture) SmallTree() =>
        MirroredTree.Realise(_temp, new TreeDirectory("cache", new TreeFile("a.bin", 4096)));

    private static HeldMftSourceFactory Held(string path, MftFixture fixture) =>
        new(char.ToUpperInvariant(Path.GetFullPath(path)[0]), fixture);

    /// <summary>
    /// The table is readable here, and walk only must not read it: the walk answers, and the result
    /// says the setting is why, so nothing offers administrator rights that would change nothing.
    /// </summary>
    [Fact]
    public async Task WalkOnlyWalksWhereTheTableCouldBeReadAndSaysWhy()
    {
        var path = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "a.bin");
        var factory = FakeMftSourceFactory.Serving(char.ToUpperInvariant(path[0]), Volume());
        var scanner = new DirectoryScanner(factory, tuning: Tuner(ScanRoute.WalkOnly).Tuner);

        var result = await scanner.MeasureAsync(path);

        Assert.Equal(ScanStrategy.ParallelEnumeration, result.Strategy);
        Assert.Equal(FallbackReason.WalkChosen, result.Fallback);
        Assert.Equal(4096, result.Size.Logical);
        Assert.Contains("Settings", result.FallbackNote!, StringComparison.Ordinal);
        Assert.Equal(0, factory.OpenCount);
        Assert.False(ElevationOffer.ShouldOffer(isElevated: false, result.Fallback, HiddenSpace.None));
    }

    /// <summary>
    /// A search by name is the table's answer too, so walk only declines it, and the caller searches
    /// by walking as it does unelevated.
    /// </summary>
    [Fact]
    public async Task WalkOnlyDeclinesASearchByName()
    {
        var factory = FakeMftSourceFactory.Serving('C', Volume());
        var (preferences, tuner) = Tuner(ScanRoute.Auto);
        var scanner = new DirectoryScanner(factory, tuning: tuner);

        Assert.NotNull(await scanner.TryFindDirectoriesNamedAsync(".npm-cache", @"C:\Users"));

        preferences.Current = preferences.Current with
        {
            Scanning = preferences.Current.Scanning with { Route = ScanRoute.WalkOnly },
        };
        scanner.Invalidate();

        Assert.Null(await scanner.TryFindDirectoriesNamedAsync(".npm-cache", @"C:\Users"));
    }

    /// <summary>The scanner is built once for the session, and the route is read as each measurement starts.</summary>
    [Fact]
    public async Task ARouteChangedAfterTheScannerWasBuiltTakesEffectFromTheNextMeasurement()
    {
        var path = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "a.bin");
        var (preferences, tuner) = Tuner(ScanRoute.Auto);
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), tuning: tuner);

        Assert.Equal(FallbackReason.NotElevated, (await scanner.MeasureAsync(path)).Fallback);

        preferences.Current = preferences.Current with
        {
            Scanning = preferences.Current.Scanning with { Route = ScanRoute.WalkOnly },
        };

        Assert.Equal(FallbackReason.WalkChosen, (await scanner.MeasureAsync(path)).Fallback);
    }

    [Fact]
    public async Task ExploreWalksUnderWalkOnlyWhereTheTableCouldBeRead()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "a.bin");
        var factory = FakeMftSourceFactory.Serving(char.ToUpperInvariant(root[0]), Volume());
        var scanner = new ExploreScanner(factory, tuning: Tuner(ScanRoute.WalkOnly).Tuner);

        var scan = await scanner.ScanAsync(root);

        Assert.Equal(ScanStrategy.ParallelEnumeration, scan.Strategy);
        Assert.Equal(FallbackReason.WalkChosen, scan.Fallback);
        Assert.Equal(0, factory.OpenCount);
        Assert.Contains("Settings", ExploreRouteText.Describe(scan.Strategy, scan.Fallback)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExploreReadsTheTableUnderAuto()
    {
        var scanner = new ExploreScanner(FakeMftSourceFactory.Serving('C', Volume()), tuning: Tuner(ScanRoute.Auto).Tuner);

        var scan = await scanner.ScanAsync(@"C:\");

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
    }

    private static (FakePreferences Preferences, ScanTuner Tuner) Tuner(ScanRoute route)
    {
        var preferences = new FakePreferences(AppPreferences.Default with
        {
            Scanning = ScanPreferences.Default with { Route = route },
        });

        return (preferences, new ScanTuner(preferences, new VolumeMediaCache(new FakeStorageQueries()), new FakeVolumeInventory()));
    }
}
