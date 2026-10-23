using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

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
        await Reopen(environment).MeasureAsync(cache, MinimumAge.Off, progress: progress);

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
        var result = await Reopen(environment).MeasureAsync(cache, MinimumAge.Off, progress: progress);

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
        await scanner.MeasureAsync(cache, MinimumAge.Off, progress: progress);

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
        var result = await Reopen(environment).MeasureAsync(cache, MinimumAge.Off, progress: progress);

        Assert.Equal(512, result.Size.Logical);
        Assert.Equal(512, progress.Reports[0].Logical);
    }

    /// <summary>
    /// A file damaged by hand or by the disk. A cache that cannot be read is a miss, never a failed
    /// scan.
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

    /// <summary>
    /// A remembered size is the whole path's, so a run with the guard on must neither show one nor
    /// leave one behind.
    ///
    /// <para>Showing it would flash a total this run is not going to reclaim. Storing the guarded
    /// figure would be worse and quieter: the next run, where the user may well have turned the
    /// guard off, would open on a number short by however much was held back — and §5.5's whole
    /// claim for that number is that it is last run's measurement of the same tree.</para>
    /// </summary>
    [Fact]
    public async Task NeitherShowsNorStoresARememberedSizeWhileTheGuardIsOn()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var cache = temp.CreateDirectory("npm-cache");
        TempDirectory.Age(temp.CreateFile(1000, "npm-cache", "a.bin"), TimeSpan.FromDays(30));
        temp.CreateFile(5000, "npm-cache", "written-just-now.bin");

        // An ordinary run, which remembers the whole tree: 6000 bytes.
        await Reopen(environment).MeasureAsync(cache);

        var guarded = new ProgressRecorder<ScanSize>();
        var result = await Reopen(environment)
            .MeasureAsync(cache, MinimumAge.WithinHours(8, DateTime.UtcNow), progress: guarded);

        Assert.Equal(1000, result.Size.Logical);
        Assert.DoesNotContain(guarded.Reports, r => r.Logical == 6000);

        // And the run after it still opens on the full measurement rather than on the guarded one.
        var unguarded = new ProgressRecorder<ScanSize>();
        await Reopen(environment).MeasureAsync(cache, MinimumAge.Off, progress: unguarded);

        Assert.Equal(6000, unguarded.Reports[0].Logical);
    }

    /// <summary>
    /// A figure with entries left out of it is short of the folder by those entries, as a guarded one
    /// is short by what the guard held back, so it is neither shown nor stored either. The next
    /// ordinary run still opens on the folder whole.
    /// </summary>
    [Fact]
    public async Task NeitherShowsNorStoresARememberedSizeWithEntriesLeftOut()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var cache = temp.CreateDirectory("temp");
        temp.CreateFile(1000, "temp", "abandoned.tmp");
        temp.CreateFile(5000, "temp", "live-session", "working.txt");

        await Reopen(environment).MeasureAsync(cache);

        var bounded = new ProgressRecorder<ScanSize>();
        var result = await Reopen(environment).MeasureAsync(
            cache,
            MinimumAge.Off,
            new ScanBounds(KeepRoot: true, [Path.Combine(cache, "live-session")]),
            bounded);

        Assert.Equal(1000, result.Size.Logical);
        Assert.DoesNotContain(bounded.Reports, r => r.Logical == 6000);

        var whole = new ProgressRecorder<ScanSize>();
        await Reopen(environment).MeasureAsync(cache, MinimumAge.Off, progress: whole);

        Assert.Equal(6000, whole.Reports[0].Logical);
    }

    /// <summary>
    /// A folder's contents and the folder whole are remembered apart. The two differ by the folder's own
    /// entry, so either run opened on the other's figure would show a count that is one out — and both
    /// are remembered, so a folder emptied in place still opens instantly.
    /// </summary>
    [Fact]
    public async Task RemembersAFoldersContentsApartFromTheFolderWhole()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var bin = temp.CreateDirectory("bin");
        temp.CreateFile(1000, "bin", "a.tmp");

        await Reopen(environment).MeasureAsync(bin);
        await Reopen(environment).MeasureAsync(bin, MinimumAge.Off, ScanBounds.Contents);

        var whole = new ProgressRecorder<ScanSize>();
        await Reopen(environment).MeasureAsync(bin, MinimumAge.Off, progress: whole);

        var contents = new ProgressRecorder<ScanSize>();
        await Reopen(environment).MeasureAsync(bin, MinimumAge.Off, ScanBounds.Contents, contents);

        Assert.Equal(2, whole.Reports[0].Entries);
        Assert.Equal(1, contents.Reports[0].Entries);
    }

    /// <summary>
    /// A reading from the disk is the freshest figure there is for each path it reads, so the next run
    /// opens on it, path by path, however many were read together. One with entries left out is short
    /// of its folder, so it replaces nothing.
    /// </summary>
    [Fact]
    public async Task OpensTheNextRunOnEachPathOfAReadingFromTheDisk()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var npm = temp.CreateDirectory("npm-cache");
        var pip = temp.CreateDirectory("pip-cache");
        var bounded = temp.CreateDirectory("temp");
        var evicted = temp.CreateFile(1000, "npm-cache", "a.bin");
        temp.CreateFile(300, "npm-cache", "b.bin");
        temp.CreateFile(2000, "pip-cache", "c.bin");
        temp.CreateFile(1000, "temp", "abandoned.tmp");
        temp.CreateFile(5000, "temp", "live-session", "working.txt");

        await Reopen(environment).MeasureManyAsync([new(npm), new(pip), new(bounded)]);

        File.Delete(evicted);

        var fresh = await Reopen(environment).MeasureFromDiskAsync(
            [new(npm), new(pip), new(bounded, new ScanBounds(KeepRoot: false, [Path.Combine(bounded, "live-session")]))]);

        Assert.Equal([300, 2000, 1000], fresh.Select(result => result.Size.Logical));

        var opened = new List<long>();

        foreach (var path in new[] { npm, pip, bounded })
        {
            var progress = new ProgressRecorder<ScanSize>();
            await Reopen(environment).MeasureAsync(path, MinimumAge.Off, progress: progress);
            opened.Add(progress.Reports[0].Logical);
        }

        Assert.Equal([300, 2000, 6000], opened);
    }
}
