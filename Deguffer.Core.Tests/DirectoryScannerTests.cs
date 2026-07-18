using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The route choice itself: which of §5.5's two strategies runs, and whether the caller can tell.
///
/// The point of these is that no provider contains this branch. If the selection is wrong here it
/// is wrong everywhere at once, which is the trade G1 makes by putting it in one place.
/// </summary>
public class DirectoryScannerTests
{
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Cache = 8;

    private static MftFixture Volume() => new MftFixture()
        .AddDirectory(Users, Deguffer.Core.Scanning.Mft.MftRecord.RootRecordNumber, "Users")
        .AddDirectory(Profile, Users, "testuser")
        .AddDirectory(Cache, Profile, ".npm-cache")
        .AddFile(20, Cache, "a.tgz", allocated: 8192, logical: 8000);

    [Fact]
    public async Task ReadsTheTableWhereItCan()
    {
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', Volume()));

        var result = await scanner.MeasureAsync(@"C:\Users\testuser\.npm-cache");

        Assert.Equal(ScanStrategy.MasterFileTable, result.Strategy);
        Assert.Equal(FallbackReason.None, result.Fallback);
        Assert.Equal(8192, result.Size.Allocated);
        Assert.Equal(8000, result.Size.Logical);
        Assert.False(result.Size.IsApproximate);
        Assert.Null(result.FallbackNote);
    }

    /// <summary>
    /// §6.3 says the app runs unelevated by default, so this is the ordinary path rather than an
    /// edge case — and §5.5 says it must be observable. A silent slow scan looks exactly like a
    /// large directory, and the user is never told that elevating would make it quick.
    /// </summary>
    [Fact]
    public async Task FallsBackObservablyWhenNotElevated()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(4096, "cache", "a.bin");

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

        var result = await scanner.MeasureAsync(Path.Combine(temp.Path, "cache"));

        Assert.Equal(ScanStrategy.ParallelEnumeration, result.Strategy);
        Assert.Equal(FallbackReason.NotElevated, result.Fallback);
        Assert.Equal(4096, result.Size.Logical);
        Assert.Contains("administrator", result.FallbackNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FallsBackWhenTheVolumeIsNotNtfs()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(2048, "cache", "a.bin");

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotNtfsVolume));

        var result = await scanner.MeasureAsync(Path.Combine(temp.Path, "cache"));

        Assert.Equal(FallbackReason.NotNtfsVolume, result.Fallback);
        Assert.Contains("not NTFS", result.FallbackNote!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A path the table cannot resolve is not an empty one. Reporting zero would render as "this
    /// cache is already clear" and hide whatever is actually there, so the walk has to answer.
    /// </summary>
    [Fact]
    public async Task FallsBackWhenTheTableCannotResolveThePath()
    {
        using var temp = new TempDirectory();
        var cache = temp.CreateDirectory("cache");
        File.WriteAllBytes(Path.Combine(cache, "a.bin"), new byte[7000]);

        // The fixture serves the volume the temp directory is on, but knows nothing of this path.
        var drive = Path.GetFullPath(temp.Path)[0];
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving(drive, Volume()));

        var result = await scanner.MeasureAsync(cache);

        Assert.Equal(ScanStrategy.ParallelEnumeration, result.Strategy);
        Assert.Equal(FallbackReason.MasterFileTableUnreadable, result.Fallback);
        Assert.Equal(7000, result.Size.Logical);
    }

    /// <summary>
    /// G5: the index is the entire cost of the fast path. Rebuilding it per query would make the
    /// MFT route slower than the walk it replaces.
    /// </summary>
    [Fact]
    public async Task BuildsTheVolumeIndexOnlyOnceAcrossManyPaths()
    {
        var factory = FakeMftSourceFactory.Serving('C', Volume());
        var scanner = new DirectoryScanner(factory);

        await scanner.MeasureAsync(@"C:\Users\testuser\.npm-cache");
        await scanner.MeasureAsync(@"C:\Users\testuser");
        await scanner.MeasureAsync(@"C:\Users");

        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task RebuildsTheIndexAfterInvalidation()
    {
        var factory = FakeMftSourceFactory.Serving('C', Volume());
        var scanner = new DirectoryScanner(factory);

        await scanner.MeasureAsync(@"C:\Users\testuser\.npm-cache");
        scanner.Invalidate();
        await scanner.MeasureAsync(@"C:\Users\testuser\.npm-cache");

        Assert.Equal(2, factory.OpenCount);
    }

    /// <summary>
    /// A failed open is remembered too. Without that, an unelevated run — the common case — loses
    /// a volume open for every path it measures.
    /// </summary>
    [Fact]
    public async Task DoesNotRetryAVolumeThatAlreadyRefused()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(16, "cache", "a.bin");
        temp.CreateFile(16, "other", "b.bin");

        var factory = FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated);
        var scanner = new DirectoryScanner(factory);

        await scanner.MeasureAsync(Path.Combine(temp.Path, "cache"));
        await scanner.MeasureAsync(Path.Combine(temp.Path, "other"));

        Assert.Equal(1, factory.OpenCount);
    }

    /// <summary>
    /// A table that could not be read in full is refused outright, so the walk answers instead. The
    /// alternative — a partial index — would report short sizes with nothing to show for it.
    /// </summary>
    [Fact]
    public async Task FallsBackWhenTheTableCouldNotBeReadInFull()
    {
        using var temp = new TempDirectory();
        var cache = temp.CreateDirectory("cache");
        File.WriteAllBytes(Path.Combine(cache, "a.bin"), new byte[3000]);

        var drive = Path.GetFullPath(temp.Path)[0];
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving(drive, Volume().UnreadableFrom(9)));

        var result = await scanner.MeasureAsync(cache);

        Assert.Equal(ScanStrategy.ParallelEnumeration, result.Strategy);
        Assert.Equal(FallbackReason.MasterFileTableUnreadable, result.Fallback);
        Assert.Equal(3000, result.Size.Logical);
    }

    [Fact]
    public async Task AcceptsAnExtendedLengthPath()
    {
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', Volume()));

        var result = await scanner.MeasureAsync(@"\\?\C:\Users\testuser\.npm-cache");

        Assert.Equal(ScanStrategy.MasterFileTable, result.Strategy);
        Assert.Equal(8192, result.Size.Allocated);
    }
}
