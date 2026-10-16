using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The measure route runs the scanner a scan runs, so its count is the scanner's: every entry a
/// removal would take, the folder itself included, and nothing reached through a link.
/// </summary>
public sealed class MeasureRouteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void CountsWhatTheRemovalWouldTake()
    {
        var root = _temp.CreateDirectory("root");
        var target = _temp.CreateDirectory("root", "a");
        _temp.CreateFile(10, "root", "a", "one.bin");
        _temp.CreateFile(10, "root", "a", "two.bin");
        _temp.CreateDirectory("root", "b");
        Junction.ToDirectory(Path.Combine(root, "link"), target);

        var tally = MeasureRoute.Run(root, new WalkTuning(1, WalkTuning.MinimumListingBuffer), CancellationToken.None);

        // root, a, b and link, and the two files in a. The link's target is not counted twice.
        Assert.Equal(6, tally.Items);
        Assert.True(tally.Complete);
    }
}
