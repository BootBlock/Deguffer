using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The scan route measures a folder as a scan does the first time it asks about the volume, so every
/// run opens the volume again, and a volume whose table cannot be read is walked, as in a scan.
/// </summary>
public sealed class AutoScanRouteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task EachRunOpensTheVolumeAgainAndCountsTheFolder()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateFile(10, "root", "a", "one.bin");
        _temp.CreateFile(10, "root", "two.bin");
        var volumes = FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated);
        var route = AutoScanRoute.On(volumes, root, ScanTuner.Shipped);

        route.Before!(CancellationToken.None);
        var first = await route.Run(CancellationToken.None);
        route.Before(CancellationToken.None);
        var second = await route.Run(CancellationToken.None);

        // The folder itself, as a removal takes it, then a, one.bin in it, and two.bin.
        Assert.Equal(4, first.Items);
        Assert.Equal(4, second.Items);
        Assert.True(first.Complete);
        Assert.Equal(2, volumes.OpenCount);
    }
}
