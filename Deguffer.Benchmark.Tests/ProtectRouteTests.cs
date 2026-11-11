using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The protect route builds its empty folders once, records every one of them on every run, and stops
/// on a record that does not match the folder it built.
/// </summary>
public sealed class ProtectRouteTests : IDisposable
{
    private const int Folders = 12;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EveryFolderIsBuiltOnceAndRecordedOnEveryRun()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new ProtectRoute(site, Folders, ScanTuner.Shipped).Timed;

        for (var run = 0; run < 2; run++)
        {
            route.Before!(CancellationToken.None);
            var tally = route.Run(CancellationToken.None);
            route.After!();

            Assert.True(tally.Complete);
            Assert.Equal(Folders, tally.Items);
            Assert.InRange(tally.PeakListings, 1, Folders);
        }

        Assert.Equal(Folders, Directory.EnumerateDirectories(site.Root, "f*").Count());
    }

    /// <summary>A folder that holds something after it was built is recorded as holding it, and that stops the benchmark.</summary>
    [Fact]
    public void ARecordThatDoesNotMatchTheFolderBuiltStopsTheBenchmark()
    {
        var site = RemovalSite.Create(Path.Combine(_temp.Path, "site"), out _)!;
        var route = new ProtectRoute(site, Folders, ScanTuner.Shipped).Timed;

        route.Before!(CancellationToken.None);
        File.WriteAllBytes(Path.Combine(site.Place("f3"), "stray.bin"), [1]);
        route.Run(CancellationToken.None);

        Assert.Contains("§5.6", Assert.Throws<InvalidOperationException>(route.After!).Message, StringComparison.Ordinal);
    }
}
