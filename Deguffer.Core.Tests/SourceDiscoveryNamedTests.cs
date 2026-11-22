using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a source-tree row picks its own folders out of the discovery every row shares, and what that
/// costs: about twenty rows ask it of every candidate in each pass.
/// </summary>
public sealed class SourceDiscoveryNamedTests
{
    private static SourceDiscovery Discovered(params string[] candidates) => new(candidates, false, [], []);

    /// <summary>
    /// A candidate is named by its last segment, whether or not the discovery wrote it with a trailing
    /// separator, ignoring case ordinally, and never by a name it only starts with or a parent it sits in.
    /// </summary>
    [Fact]
    public void PicksTheCandidatesWhoseLastSegmentIsOneOfTheNamesInDiscoveryOrder()
    {
        var discovery = Discovered(
            @"C:\Users\testuser\source\app\bin",
            @"C:\Users\testuser\source\app\binaries",
            @"C:\Users\testuser\source\app\OBJ\",
            @"C:\Users\testuser\source\bin\app",
            @"\\?\C:\Users\testuser\source\deep\Obj",
            @"C:\Users\testuser\source\lib\objx\",
            @"C:\Users\testuser\source\lib\Bin\\",
            @"C:\Users\testuser\source\lib\xbin");

        Assert.Equal(
            [
                @"C:\Users\testuser\source\app\bin",
                @"C:\Users\testuser\source\app\OBJ\",
                @"\\?\C:\Users\testuser\source\deep\Obj",
                @"C:\Users\testuser\source\lib\Bin\\",
            ],
            discovery.Named(["obj", "bin"]));
    }

    [Fact]
    public void NoNamesPicksNothing() =>
        Assert.Empty(Discovered(@"C:\Users\testuser\source\app\bin").Named([]));

    /// <summary>
    /// Every row asks this of every candidate, so a candidate's name is compared where it sits in the
    /// path rather than cut out of it: the only thing built is the list the row keeps.
    /// </summary>
    [Fact]
    public void PickingFromTenThousandCandidatesBuildsOnlyTheListItKeeps()
    {
        const int Candidates = 10_000;
        var discovery = Discovered(
        [
            .. Enumerable.Range(0, Candidates).Select(candidate => candidate % 100 == 0
                ? $@"C:\Users\testuser\source\project-{candidate}\bin\"
                : $@"C:\Users\testuser\source\project-{candidate}\node_modules"),
        ]);
        IReadOnlyList<string> names = ["obj", "bin"];

        discovery.Named(names);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var named = discovery.Named(names);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Candidates / 100, named.Count);
        Assert.InRange(allocated, 0, 4 * 1024);
    }
}
