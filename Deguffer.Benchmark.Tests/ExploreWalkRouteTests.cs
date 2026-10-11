using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The Explore walk the benchmark times builds the tree Explore builds, whether or not it takes
/// snapshots on the way, so the two timings describe the same work but for the snapshots.
/// </summary>
public sealed class ExploreWalkRouteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CountsEveryEntryOnceWithOrWithoutSnapshots(bool snapshots)
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateFile(10, "root", "a", "one.bin");
        _temp.CreateFile(10, "root", "a", "two.bin");
        _temp.CreateDirectory("root", "b");

        var tally = ExploreWalkRoute.Run(root, WalkTuning.Default, snapshots, CancellationToken.None);

        // a and b at the top, and the two files in a.
        Assert.Equal(4, tally.Items);
        Assert.Equal(0, tally.BytesRead);
        Assert.True(tally.Complete);
    }

    [Fact]
    public void AFolderItWasRefusedLeavesTheRunIncomplete()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateFile(10, "root", "open", "file.bin");
        var refused = _temp.CreateDirectory("root", "refused");
        _temp.CreateFile(10, "root", "refused", "hidden.bin");

        using var denied = new DeniedDirectory(refused);

        var tally = ExploreWalkRoute.Run(root, WalkTuning.Default, snapshots: true, CancellationToken.None);

        // open, refused and the file in open. Nothing inside the refused folder.
        Assert.Equal(3, tally.Items);
        Assert.False(tally.Complete);
    }
}
