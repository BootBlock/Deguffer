using System.Collections.Concurrent;
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

    /// <summary>
    /// The state-carrying overload hands each directory back whatever its parent chose for it, which
    /// is how a caller building a structure keeps its place. Nothing else in the callback says which
    /// directory is being read, so a state delivered to the wrong child produces a tree that is
    /// entirely well formed and describes a different disk.
    /// </summary>
    [Fact]
    public void CarriesTheCallersStateDownToTheChildItWasChosenFor()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(16, "cache", "one", "a.bin");
        _temp.CreateFile(16, "cache", "one", "deeper", "b.bin");
        _temp.CreateFile(16, "cache", "two", "c.bin");

        var seen = new ConcurrentBag<(string State, string Entries)>();

        BoundedFileWalk.Visit(
            root,
            "cache",
            (state, contents, descend) =>
            {
                seen.Add((state, string.Join(", ", contents.Entries.Select(e => e.Name).Order(StringComparer.Ordinal))));

                foreach (var entry in contents.Entries)
                {
                    if (entry is DirectoryInfo directory)
                    {
                        descend(directory, directory.Name);
                    }
                }
            },
            () => { },
            default);

        Assert.Equal(
            [("cache", "one, two"), ("deeper", "b.bin"), ("one", "a.bin, deeper"), ("two", "c.bin")],
            seen.Order());
    }

    /// <summary>
    /// A link is reported and is never something the caller may descend into. The rule lives in the
    /// walk rather than in each of its three callers, so it is asserted here: the junction is present
    /// among the links, and the file inside its target is visited exactly once — through the target's
    /// own place in the tree, and not again through the name pointing at it.
    /// </summary>
    [Fact]
    public void ReportsALinkSeparatelySoNoCallerCanDescendThroughIt()
    {
        var root = _temp.CreateDirectory("cache");
        var real = _temp.CreateDirectory("cache", "content-v2");
        _temp.CreateFile(32, "cache", "content-v2", "inside.bin");

        Directory.CreateSymbolicLink(Path.Combine(root, "shortcut"), real);

        var entries = new ConcurrentBag<string>();
        var links = new ConcurrentBag<string>();

        BoundedFileWalk.Visit<byte>(
            root,
            0,
            (_, contents, descend) =>
            {
                foreach (var entry in contents.Entries)
                {
                    entries.Add(entry.Name);

                    if (entry is DirectoryInfo directory)
                    {
                        descend(directory, 0);
                    }
                }

                foreach (var link in contents.Links)
                {
                    links.Add(link.Name);
                }
            },
            () => { },
            default);

        Assert.Equal(["shortcut"], links.Order(StringComparer.Ordinal));
        Assert.Equal(["content-v2", "inside.bin"], entries.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// §5.3 again, from the other side. A directory that could not be listed is still skipped
    /// silently, but it is now distinguishable from one that is genuinely empty — without that, a
    /// caller reporting a total has no way to know the total is a lower bound, and reports it as a
    /// measurement.
    /// </summary>
    [Fact]
    public void SaysWhichDirectoryItWasRefusedRatherThanReportingItEmpty()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateDirectory("cache", "empty");
        var refused = _temp.CreateDirectory("cache", "refused");
        _temp.CreateFile(64, "cache", "refused", "unreachable.bin");

        using var denied = new DeniedDirectory(refused);

        var outcomes = new ConcurrentBag<(string Directory, bool Refused, int Entries)>();

        BoundedFileWalk.Visit(
            root,
            "cache",
            (state, contents, descend) =>
            {
                outcomes.Add((state, contents.WasRefused, contents.Entries.Count));

                foreach (var entry in contents.Entries)
                {
                    if (entry is DirectoryInfo directory)
                    {
                        descend(directory, directory.Name);
                    }
                }
            },
            () => { },
            default);

        Assert.Equal(
            [("cache", false, 2), ("empty", false, 0), ("refused", true, 0)],
            outcomes.Order());
    }

    /// <summary>
    /// §6.3 for the state-carrying overload, asserted the same discriminating way as for the plain
    /// one: what a long-path fixture can actually prove is that the prefix propagates, not that a
    /// deep tree was reached. Both overloads run the same traversal, and this is the one three
    /// callers now use.
    /// </summary>
    [Fact]
    public void CarriesTheFormOfTheRootDownToEveryDirectoryTheStatefulWalkVisits()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(64, "cache", "top.bin");
        _temp.CreateFile(64, "cache", "nested", "deeper", "leaf.bin");

        Assert.All(Reached(LongPath.Extended(root)), p => Assert.StartsWith(@"\\?\", p, StringComparison.Ordinal));
        Assert.All(Reached(root), p => Assert.False(p.StartsWith(@"\\?\", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Every entry the state-carrying walk hands back, by the path it was handed back under.
    /// </summary>
    private static List<string> Reached(string root)
    {
        var seen = new ConcurrentBag<string>();

        BoundedFileWalk.Visit<byte>(
            root,
            0,
            (_, contents, descend) =>
            {
                foreach (var entry in contents.Entries)
                {
                    seen.Add(entry.FullName);

                    if (entry is DirectoryInfo directory)
                    {
                        descend(directory, 0);
                    }
                }
            },
            () => { },
            default);

        Assert.Equal(4, seen.Count);
        return [.. seen];
    }

    private static List<string> Visited(string root)
    {
        var seen = new List<string>();

        BoundedFileWalk.Visit(root, file => seen.Add(file.FullName), () => { }, default);

        Assert.Equal(2, seen.Count);
        return seen;
    }
}
