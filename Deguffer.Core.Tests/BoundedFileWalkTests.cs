using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.3's other half, which had no test in either scanner until the walk became one seam.
///
/// "Treat 'access denied' as normal and skip silently — a locked file is the OS protecting live
/// state" is a safety rule, and an untested one is a rule that can be deleted without anything
/// noticing: removing the catch filter outright left the whole suite green. It is tested here
/// rather than in a scanner's own class because both scanners now reach it through
/// <see cref="BoundedFileWalk"/>, so one test covers both.
/// </summary>
public sealed class BoundedFileWalkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The scan reports what it could read and does not fail, which is §5.3 exactly. Both halves
    /// matter: an exception here would take a whole preview down over one protected folder, and
    /// counting the unreadable subtree would promise bytes no deletion could reclaim.
    /// </summary>
    [Fact]
    public async Task ARefusedDirectoryIsSkippedAndTheRestOfTheTreeStillCounts()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "readable.bin");
        var refused = _temp.CreateDirectory("cache", "refused");
        _temp.CreateFile(65536, "cache", "refused", "unreachable.bin");

        using var denied = new DeniedDirectory(refused);

        var measured = await ParallelEnumerationScanner.Default.MeasureAsync(root);

        Assert.Equal(4096, measured.Size.Logical);
    }

    /// <summary>
    /// §6.3, and the claim <see cref="BoundedFileWalk.Visit"/> already makes in its own parameter
    /// documentation: hand it an extended-length root and every path it hands back carries the
    /// prefix too, because .NET builds each child from the parent it was given.
    ///
    /// <para>That claim had no test, and it is the one thing about the walk a long-path fixture can
    /// actually discriminate. Asserting that a deep tree was measured proves nothing — .NET
    /// prefixes past 260 characters on its own, so such a test passes with the prefixing deleted
    /// outright. <see cref="LongPathTests.TheRuntimeStillReachesPastMaxPathWithoutOurPrefix"/> is
    /// where that is established.</para>
    ///
    /// <para>Both directions are asserted, because the propagation is the whole mechanism: a plain
    /// root yields plain children, so a caller that forgets to extend gets no prefix anywhere below
    /// it either, however deep the tree runs.</para>
    /// </summary>
    [Fact]
    public void CarriesTheFormOfTheRootDownToEveryFileItVisits()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(64, "cache", "top.bin");
        _temp.CreateFile(64, "cache", "nested", "deeper", "leaf.bin");

        Assert.All(Visited(LongPath.Extended(root)), p => Assert.StartsWith(@"\\?\", p, StringComparison.Ordinal));
        Assert.All(Visited(root), p => Assert.False(p.StartsWith(@"\\?\", StringComparison.Ordinal)));
    }

    private static List<string> Visited(string root)
    {
        var seen = new List<string>();

        BoundedFileWalk.Visit(root, file => seen.Add(file.FullName), () => { }, default);

        Assert.Equal(2, seen.Count);
        return seen;
    }
}
