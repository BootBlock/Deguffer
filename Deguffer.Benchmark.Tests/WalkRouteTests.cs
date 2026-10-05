using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The walk the benchmark times is the walk a scan takes: every entry listed once, nothing reached
/// through a link, and a refused folder reported.
///
/// <para>No test here claims §6.3. The route's only output is a count, and a deep tree is walked to
/// its end whether or not the root carries the extended prefix, because .NET adds it past
/// <c>MAX_PATH</c> on its own. <c>BoundedFileWalk</c>'s own tests assert the form of the paths it
/// visits, which is the assertion that can fail.</para>
/// </summary>
public sealed class WalkRouteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A junction is listed as one entry and never walked through. Followed, it would count its
    /// target's two files a second time, and a result would be timing a larger tree than it names.
    /// </summary>
    [Fact]
    public void CountsEveryEntryOnceAndNeverWalksThroughALink()
    {
        var root = _temp.CreateDirectory("root");
        var target = _temp.CreateDirectory("root", "a");
        _temp.CreateFile(10, "root", "a", "one.bin");
        _temp.CreateFile(10, "root", "a", "two.bin");
        _temp.CreateDirectory("root", "b");
        Junction.ToDirectory(Path.Combine(root, "link"), target);

        var tally = WalkRoute.Run(root, CancellationToken.None);

        // a, b and link at the top, and the two files in a.
        Assert.Equal(5, tally.Items);
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

        var tally = WalkRoute.Run(root, CancellationToken.None);

        // open, refused and the file in open. Nothing inside the refused folder.
        Assert.Equal(3, tally.Items);
        Assert.False(tally.Complete);
    }
}
