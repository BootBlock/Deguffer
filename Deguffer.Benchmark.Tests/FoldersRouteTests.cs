using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The folders route the benchmark times counts every pass of the scan together, and is complete only
/// where nothing was refused it. Its tables are refused here, so it walks the test's own folders
/// rather than reading the machine's.
/// </summary>
public sealed class FoldersRouteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void CountsTheEntriesOfEveryFolderTogether()
    {
        var first = _temp.CreateDirectory("first");
        _temp.CreateFile(10, "first", "one.bin");
        _temp.CreateFile(10, "first", "a", "two.bin");
        var second = _temp.CreateDirectory("second");
        _temp.CreateFile(10, "second", "three.bin");

        var tally = FoldersRoute.Run(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), [first, second], CancellationToken.None);

        // one.bin, a and two.bin in the first, and three.bin in the second.
        Assert.Equal(4, tally.Items);
        Assert.True(tally.Complete);
    }

    [Fact]
    public void AFolderItWasRefusedLeavesTheRunIncomplete()
    {
        var first = _temp.CreateDirectory("first");
        var refused = _temp.CreateDirectory("first", "refused");
        _temp.CreateFile(10, "first", "refused", "hidden.bin");
        var second = _temp.CreateDirectory("second");

        using var denied = new DeniedDirectory(refused);

        var tally = FoldersRoute.Run(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), [first, second], CancellationToken.None);

        Assert.False(tally.Complete);
    }
}
