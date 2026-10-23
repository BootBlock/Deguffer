using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How often the remembered sizes reach the disk. A pass measures its targets one after another, and
/// on a first run nearly every figure is new, so a write for each would write the whole file as often
/// as there are targets. The figures are written once, when the pass that measured them ends.
/// </summary>
public class ScanEstimateWriteTests
{
    /// <summary>Counts the cache's writes, and fails the next one where asked to.</summary>
    private sealed class CountingStore
    {
        public int Writes { get; private set; }

        public bool FailNext { get; set; }

        public void Store(string file, string contents)
        {
            Writes++;

            if (FailNext)
            {
                FailNext = false;
                throw new IOException("The disk is full.");
            }

            StoreFile.Replace(file, contents);
        }
    }

    private static DirectoryScanner Scanner(ScanEstimateCache estimates) =>
        new(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), estimates);

    /// <summary>The first figure the next run shows for <paramref name="path"/>.</summary>
    private static async Task<long> OpensOn(IUserEnvironment environment, string path)
    {
        var progress = new ProgressRecorder<ScanSize>();
        await Scanner(new ScanEstimateCache(environment)).MeasureAsync(path, MinimumAge.Off, progress: progress);

        return progress.Reports[0].Logical;
    }

    [Fact]
    public async Task WritesOnceForAPassOfManyChangedMeasurements()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var store = new CountingStore();
        var scanner = Scanner(new ScanEstimateCache(environment, store.Store));
        var paths = Enumerable.Range(1, 5).Select(i => temp.CreateDirectory($"cache-{i}")).ToArray();

        for (var i = 0; i < paths.Length; i++)
        {
            temp.CreateFile(100 * (i + 1), $"cache-{i + 1}", "a.bin");
        }

        using (scanner.Hold())
        {
            foreach (var path in paths)
            {
                await scanner.MeasureAsync(path);
            }

            Assert.Equal(0, store.Writes);
        }

        Assert.Equal(1, store.Writes);

        var opened = new List<long>();

        foreach (var path in paths)
        {
            opened.Add(await OpensOn(environment, path));
        }

        Assert.Equal([100, 200, 300, 400, 500], opened);
    }

    /// <summary>
    /// Every provider of a pass takes a hold on the scanner they share, so the first to give its hold
    /// back must not write what the rest are still measuring.
    /// </summary>
    [Fact]
    public async Task WritesWhenTheLastHoldOfAPassEnds()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var store = new CountingStore();
        var scanner = Scanner(new ScanEstimateCache(environment, store.Store));
        var npm = temp.CreateDirectory("npm-cache");
        var pip = temp.CreateDirectory("pip-cache");
        temp.CreateFile(1000, "npm-cache", "a.bin");
        temp.CreateFile(2000, "pip-cache", "b.bin");

        var first = scanner.Hold();
        var second = scanner.Hold();

        await scanner.MeasureAsync(npm);
        first.Dispose();
        await scanner.MeasureAsync(pip);

        Assert.Equal(0, store.Writes);

        second.Dispose();

        Assert.Equal(1, store.Writes);
        Assert.Equal(2000, await OpensOn(environment, pip));
    }

    [Fact]
    public async Task KeepsTheLastFigureOfAPathMeasuredTwiceInOnePass()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var store = new CountingStore();
        var scanner = Scanner(new ScanEstimateCache(environment, store.Store));
        var cache = temp.CreateDirectory("npm-cache");
        temp.CreateFile(1000, "npm-cache", "a.bin");

        using (scanner.Hold())
        {
            await scanner.MeasureAsync(cache);
            temp.CreateFile(5000, "npm-cache", "b.bin");
            await scanner.MeasureAsync(cache);
        }

        Assert.Equal(1, store.Writes);
        Assert.Equal(6000, await OpensOn(environment, cache));
    }

    /// <summary>
    /// The executor's readings and a page's own measurements are taken with no pass standing, and
    /// nothing would end to write them. Each call is written as it ends, once however many paths it read.
    /// </summary>
    [Fact]
    public async Task WritesEachMeasurementCallOutsideAPassOnceAsItEnds()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var store = new CountingStore();
        var scanner = Scanner(new ScanEstimateCache(environment, store.Store));
        var paths = Enumerable.Range(1, 3).Select(i => temp.CreateDirectory($"cache-{i}")).ToArray();

        for (var i = 0; i < paths.Length; i++)
        {
            temp.CreateFile(100 * (i + 1), $"cache-{i + 1}", "a.bin");
        }

        await scanner.MeasureManyAsync([.. paths.Select(path => new ScanRequest(path))]);

        Assert.Equal(1, store.Writes);

        temp.CreateFile(1000, "cache-1", "b.bin");
        temp.CreateFile(1000, "cache-2", "b.bin");
        await scanner.MeasureFromDiskAsync([.. paths.Select(path => new ScanRequest(path))]);

        Assert.Equal(2, store.Writes);

        temp.CreateFile(1000, "cache-3", "b.bin");
        await scanner.MeasureAsync(paths[2]);

        Assert.Equal(3, store.Writes);
        Assert.Equal(1300, await OpensOn(environment, paths[2]));
    }

    [Fact]
    public async Task WritesNothingForAPassThatChangedNoFigure()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var store = new CountingStore();
        var scanner = Scanner(new ScanEstimateCache(environment, store.Store));
        var cache = temp.CreateDirectory("npm-cache");
        temp.CreateFile(1000, "npm-cache", "a.bin");

        await scanner.MeasureAsync(cache);

        using (scanner.Hold())
        {
            await scanner.MeasureAsync(cache);
        }

        Assert.Equal(1, store.Writes);
    }

    /// <summary>
    /// The window closed mid-preview, or the elevated relaunch: the pass under way never ends in this
    /// process, so its figures are written when the app asks, and the pass ending later writes nothing
    /// more.
    /// </summary>
    [Fact]
    public async Task SavesThePassUnderWayWhenAsked()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var store = new CountingStore();
        var estimates = new ScanEstimateCache(environment, store.Store);
        var scanner = Scanner(estimates);
        var cache = temp.CreateDirectory("npm-cache");
        temp.CreateFile(1000, "npm-cache", "a.bin");

        var pass = scanner.Hold();
        await scanner.MeasureAsync(cache);

        estimates.Save();

        Assert.Equal(1, store.Writes);
        Assert.Equal(1000, await OpensOn(environment, cache));

        pass.Dispose();

        Assert.Equal(1, store.Writes);
    }

    /// <summary>A write the disk refused is kept as owed, so the next save carries the figures it lost.</summary>
    [Fact]
    public async Task TriesAFailedWriteAgainAtTheNextSave()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var store = new CountingStore { FailNext = true };
        var estimates = new ScanEstimateCache(environment, store.Store);
        var scanner = Scanner(estimates);
        var cache = temp.CreateDirectory("npm-cache");
        temp.CreateFile(1000, "npm-cache", "a.bin");

        await scanner.MeasureAsync(cache);

        Assert.Equal(1, store.Writes);
        Assert.False(LongPath.FileExists(Path.Combine(environment.LocalAppData, "Deguffer", "scan-estimates.json")));

        estimates.Save();

        Assert.Equal(2, store.Writes);
        Assert.Equal(1000, await OpensOn(environment, cache));
    }
}
