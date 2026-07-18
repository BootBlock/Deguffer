using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.5's "cache results with invalidation, so re-opening the tool is instant".
///
/// The contract under test is narrow and load-bearing: a remembered size may reach the *screen*
/// immediately, and may never be the figure returned to a caller. <see cref="Execution.PlanExecutor"/>
/// reports what an eviction command reclaimed by subtracting the after-size from the plan-time
/// estimate, so a stale value returned as authoritative would overstate reclaimed space rather
/// than merely looking wrong.
/// </summary>
public class ScanEstimateCacheTests
{
    private static DirectoryScanner Reopen(IUserEnvironment environment) =>
        new(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), new ScanEstimateCache(environment));

    [Fact]
    public async Task ShowsTheRememberedSizeImmediatelyOnReopening()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var cache = temp.CreateDirectory("npm-cache");
        File.WriteAllBytes(Path.Combine(cache, "a.bin"), new byte[1000]);

        await Reopen(environment).MeasureAsync(cache);

        var progress = new ProgressRecorder<ScanSize>();
        await Reopen(environment).MeasureAsync(cache, progress);

        // The first thing the second run reports is last run's number, before any walking.
        Assert.Equal(1000, progress.Reports[0].Logical);
    }

    /// <summary>
    /// The whole reason the cache is display-only. If the tree changed while the tool was closed,
    /// the returned figure must describe the tree as it is now.
    /// </summary>
    [Fact]
    public async Task NeverReturnsARememberedSizeAsTheAnswer()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var cache = temp.CreateDirectory("npm-cache");
        File.WriteAllBytes(Path.Combine(cache, "a.bin"), new byte[1000]);

        await Reopen(environment).MeasureAsync(cache);

        // The cache grows while the tool is closed.
        File.WriteAllBytes(Path.Combine(cache, "b.bin"), new byte[5000]);

        var progress = new ProgressRecorder<ScanSize>();
        var result = await Reopen(environment).MeasureAsync(cache, progress);

        Assert.Equal(6000, result.Size.Logical);
        Assert.Equal(1000, progress.Reports[0].Logical);
        Assert.Equal(6000, progress.Reports[^1].Logical);
    }

    /// <summary>
    /// Invalidate runs at the *start* of a planning pass. Dropping remembered sizes there would
    /// throw away the values that make the window populate instantly — the point of having them.
    /// </summary>
    [Fact]
    public async Task KeepsRememberedSizesAcrossInvalidation()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var cache = temp.CreateDirectory("npm-cache");
        File.WriteAllBytes(Path.Combine(cache, "a.bin"), new byte[2048]);

        var scanner = Reopen(environment);
        await scanner.MeasureAsync(cache);

        scanner.Invalidate();

        var progress = new ProgressRecorder<ScanSize>();
        await scanner.MeasureAsync(cache, progress);

        Assert.Equal(2048, progress.Reports[0].Logical);
    }

    [Fact]
    public async Task StartsFromNothingWhenNoCacheHasBeenWritten()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var cache = temp.CreateDirectory("npm-cache");
        File.WriteAllBytes(Path.Combine(cache, "a.bin"), new byte[512]);

        var progress = new ProgressRecorder<ScanSize>();
        var result = await Reopen(environment).MeasureAsync(cache, progress);

        Assert.Equal(512, result.Size.Logical);
        Assert.Equal(512, progress.Reports[0].Logical);
    }

    /// <summary>
    /// The file is rewritten on every measurement, so an interrupted process can leave it
    /// truncated. A cache that cannot be read is a miss, never a failed scan.
    /// </summary>
    [Fact]
    public async Task ToleratesACorruptCacheFile()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var cache = temp.CreateDirectory("npm-cache");
        File.WriteAllBytes(Path.Combine(cache, "a.bin"), new byte[4096]);

        await Reopen(environment).MeasureAsync(cache);

        var file = Path.Combine(environment.LocalAppData, "Deguffer", "scan-estimates.json");
        Assert.True(LongPath.FileExists(file));
        File.WriteAllText(file, "{ not json");

        var result = await Reopen(environment).MeasureAsync(cache);

        Assert.Equal(4096, result.Size.Logical);
    }
}
