using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The route choice: Auto reads the table wherever it can, as every scan did before the choice
/// existed, and walk only walks even where the table is readable, and says why.
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

    [Fact]
    public async Task AutoReadsTheTableWhereItCan()
    {
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', Volume()), tuning: Tuner(ScanRoute.Auto).Tuner);

        var result = await scanner.MeasureAsync(@"C:\Users\testuser\.npm-cache");

        Assert.Equal(ScanStrategy.MasterFileTable, result.Strategy);
    }

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
        Assert.False(ElevationOffer.ShouldOffer(isElevated: false, result.Fallback));
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
