using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The search the benchmark times is the search a scan makes: with every name the source folder rows
/// look for, stopping at what it finds, and with a refused folder reported.
/// </summary>
public sealed class SourcesRouteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The names come from building the rows, so a row added or changed is timed without an edit
    /// here. A few rows' names are checked to show the set is theirs rather than empty or a stand-in.
    /// </summary>
    [Fact]
    public void SearchesForTheNamesTheSourceFolderRowsRegister() =>
        Assert.Superset(
            new HashSet<string>(["obj", "node_modules", "target", "Library", ".venv", "venv"], StringComparer.OrdinalIgnoreCase),
            SourcesRoute.Names.ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Each build output counts once, and nothing below one is found again: an <c>obj</c> inside an
    /// <c>obj</c>, or a package's own <c>obj</c> inside <c>node_modules</c>.
    /// </summary>
    [Fact]
    public void CountsEachBuildOutputOnce()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateDirectory("root", "app", "obj", "obj");
        _temp.CreateDirectory("root", "web", "node_modules", "left-pad", "obj");
        _temp.CreateDirectory("root", "tool", "src");

        var tally = SourcesRoute.Run(root, WalkTuning.Default, CancellationToken.None);

        Assert.Equal(2, tally.Items);
        Assert.Equal(0, tally.BytesRead);
        Assert.True(tally.Complete);
    }

    [Fact]
    public void AFolderItWasRefusedLeavesTheRunIncomplete()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateDirectory("root", "app", "obj");
        var refused = _temp.CreateDirectory("root", "refused");
        _temp.CreateDirectory("root", "refused", "obj");

        using var denied = new DeniedDirectory(refused);

        var tally = SourcesRoute.Run(root, WalkTuning.Default, CancellationToken.None);

        Assert.Equal(1, tally.Items);
        Assert.False(tally.Complete);
    }
}
