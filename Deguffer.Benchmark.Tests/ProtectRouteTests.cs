using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The protect route builds its empty folders once, records every one of them on every run, and stops
/// on a record that does not match the folder it built, whether each folder is asked of the disk or
/// recorded from a listing of the folder holding them.
/// </summary>
public sealed class ProtectRouteTests : IDisposable
{
    private const int Folders = 12;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryFolderIsRecordedOnEveryRun(bool listed)
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new ProtectRoute(site, Folders, listed, ScanTuner.Shipped).Timed;

        for (var run = 0; run < 2; run++)
        {
            await route.Before!(CancellationToken.None);
            var tally = await route.Run(CancellationToken.None);
            await route.After!();

            Assert.True(tally.Complete);
            Assert.Equal(Folders, tally.Items);
            Assert.InRange(tally.PeakListings, 1, Folders);
        }

        Assert.Equal(Folders, Directory.EnumerateDirectories(site.Root, "f*").Count());
    }

    /// <summary>
    /// The folders are built before the first run only: one removed after it is not built again, so
    /// later runs time the reads alone.
    /// </summary>
    [Fact]
    public async Task TheFoldersAreBuiltBeforeTheFirstRunOnly()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new ProtectRoute(site, Folders, listed: false, ScanTuner.Shipped).Timed;

        await route.Before!(CancellationToken.None);
        Directory.Delete(site.Place("f5"));
        await route.Before!(CancellationToken.None);

        Assert.False(Directory.Exists(site.Place("f5")));
        Assert.Equal(Folders - 1, Directory.EnumerateDirectories(site.Root, "f*").Count());
    }

    /// <summary>
    /// A folder that holds something after it was built is recorded as holding it, and that stops the
    /// benchmark. From a listing too: the listing proves each folder is there, and what it holds is
    /// still asked.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARecordThatDoesNotMatchTheFolderBuiltStopsTheBenchmark(bool listed)
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new ProtectRoute(site, Folders, listed, ScanTuner.Shipped).Timed;

        await route.Before!(CancellationToken.None);
        File.WriteAllBytes(Path.Combine(site.Place("f3"), "stray.bin"), [1]);
        await route.Run(CancellationToken.None);

        Assert.Contains("§5.6", (await Assert.ThrowsAsync<InvalidOperationException>(async () => await route.After!())).Message, StringComparison.Ordinal);
    }
}
