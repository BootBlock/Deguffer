using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The verify route builds its empty folders once, proves every one of them standing on every run, and
/// stops on a folder it built that the verification does not prove standing.
/// </summary>
public sealed class VerifyRouteTests : IDisposable
{
    private const int Folders = 12;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task EveryFolderIsProvedStandingOnEveryRun()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new VerifyRoute(site, Folders, ScanTuner.Shipped).Timed;

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
    /// later runs time the checks alone.
    /// </summary>
    [Fact]
    public async Task TheFoldersAreBuiltBeforeTheFirstRunOnly()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new VerifyRoute(site, Folders, ScanTuner.Shipped).Timed;

        await route.Before!(CancellationToken.None);
        Directory.Delete(site.Place("f5"));
        await route.Before!(CancellationToken.None);

        Assert.False(Directory.Exists(site.Place("f5")));
        Assert.Equal(Folders - 1, Directory.EnumerateDirectories(site.Root, "f*").Count());
    }

    /// <summary>A folder the route built that has gone is not proved standing, and that stops the benchmark.</summary>
    [Fact]
    public async Task AFolderNotProvedStandingStopsTheBenchmark()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new VerifyRoute(site, Folders, ScanTuner.Shipped).Timed;

        await route.Before!(CancellationToken.None);
        Directory.Delete(site.Place("f3"));
        await route.Run(CancellationToken.None);

        Assert.Contains("§5.6", (await Assert.ThrowsAsync<InvalidOperationException>(async () => await route.After!())).Message, StringComparison.Ordinal);
    }
}
