using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.5's guaranteed route, against a real tree on disk.
///
/// <para>Real directories rather than a fake, because what is under test is precisely what the
/// filesystem does: a listing the account is refused, a junction that appears to hold a subtree it
/// does not, and a file whose length has to be asked for. None of those can be modelled by
/// something that answers the way the test expects.</para>
/// </summary>
public sealed class WalkExploreReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReproducesEveryDirectoryAndFileWithItsSizeAndItsParent()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "a.tgz");
        _temp.CreateFile(1024, "cache", "content-v2", "b.tgz");
        _temp.CreateFile(2048, "cache", "content-v2", "sha512", "c.tgz");
        _temp.CreateDirectory("cache", "empty");

        var tree = WalkExploreReader.Read(root, onLevel: null, default);
        var byPath = ByPath(tree);

        Assert.Equal(
            [root, .. Paths(root, @"a.tgz", "content-v2", "empty", @"content-v2\b.tgz", @"content-v2\sha512",
                @"content-v2\sha512\c.tgz")],
            byPath.Keys.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(4096, tree.SizeOf(byPath[Path.Combine(root, "a.tgz")]));
        Assert.Equal(2048, tree.SizeOf(byPath[Path.Combine(root, @"content-v2\sha512")]));
        Assert.Equal(3072, tree.SizeOf(byPath[Path.Combine(root, "content-v2")]));
        Assert.Equal(0, tree.SizeOf(byPath[Path.Combine(root, "empty")]));
        Assert.Equal(7168, tree.TotalBytes);

        Assert.Equal(
            byPath[Path.Combine(root, "content-v2")],
            tree.ParentOf(byPath[Path.Combine(root, @"content-v2\sha512")]));

        Assert.True(tree.IsDirectory(byPath[Path.Combine(root, "content-v2")]));
        Assert.False(tree.IsDirectory(byPath[Path.Combine(root, "a.tgz")]));
        Assert.False(tree.HasUnknownSizes);
    }

    /// <summary>
    /// §5.3, and the half of it the walk alone cannot express. Skipping a refused directory silently
    /// is right; presenting the total that results as a measurement is not, because the bytes behind
    /// it are real and unmeasured.
    ///
    /// <para>Both halves are asserted. The scan does not throw and counts what it could read, which
    /// is §5.3 exactly — and every total from the refused directory up to the root says it is a
    /// lower bound, while a sibling that was fully read still says it is not.</para>
    /// </summary>
    [Fact]
    public void MarksTheTotalsAboveARefusedDirectoryAsLowerBounds()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "readable.bin");
        _temp.CreateFile(512, "cache", "logs", "a.log");
        var refused = _temp.CreateDirectory("cache", "content-v2", "refused");
        _temp.CreateFile(65536, "cache", "content-v2", "refused", "unreachable.bin");

        using var denied = new DeniedDirectory(refused);

        var tree = WalkExploreReader.Read(root, onLevel: null, default);
        var byPath = ByPath(tree);

        Assert.Equal(4608, tree.TotalBytes);
        Assert.True(tree.HasUnknownSizes);
        Assert.True(tree.HasUnknownSizeBelow(byPath[refused]));
        Assert.True(tree.HasUnknownSizeBelow(byPath[Path.Combine(root, "content-v2")]));
        Assert.False(tree.HasUnknownSizeBelow(byPath[Path.Combine(root, "logs")]));
    }

    /// <summary>
    /// A junction is shown and holds nothing. Its target keeps its own place in the tree, so
    /// counting through one would report the same bytes twice and draw a subtree the walk never
    /// classified — while hiding it altogether makes a directory the user can plainly see in
    /// Explorer vanish from the picture.
    ///
    /// <para>Both of those are asserted, because each alone passes for the wrong reason: a reader
    /// that dropped links entirely would satisfy "nothing appears twice", and one that followed them
    /// would satisfy "the link is present".</para>
    /// </summary>
    [Fact]
    public void ShowsAJunctionAsAnEmptyLinkAndNeverCountsItsTargetTwice()
    {
        var root = _temp.CreateDirectory("cache");
        var real = _temp.CreateDirectory("cache", "content-v2");
        _temp.CreateFile(2048, "cache", "content-v2", "inside.bin");

        Directory.CreateSymbolicLink(Path.Combine(root, "shortcut"), real);

        var tree = WalkExploreReader.Read(root, onLevel: null, default);
        var byPath = ByPath(tree);
        var link = byPath[Path.Combine(root, "shortcut")];

        Assert.True(tree.IsLink(link));
        Assert.True(tree.IsDirectory(link));
        Assert.Equal(0, tree.SizeOf(link));
        Assert.Empty(tree.ChildrenOf(link).ToArray());

        Assert.Equal(1, CountNamed(tree, "inside.bin"));
        Assert.Equal(2048, tree.TotalBytes);
        Assert.False(tree.IsLink(byPath[real]));
    }

    /// <summary>
    /// §6.3, asserted on the <em>form</em> of the paths rather than on the depth of a tree.
    ///
    /// <para>The reader is handed a root in either form and gives the walk the extended one, so that
    /// the traversal stays past <c>MAX_PATH</c>; the tree keeps the display form, because every path
    /// it hands back is one a person reads or a shell opens. Only the second half of that is
    /// discriminating here — a tree of leaf names has no other observable that changes when the
    /// prefix is dropped on the way in, and CLAUDE.md's G8 says to name that rather than write a
    /// deep-tree test that cannot fail. The propagation of the prefix <em>through</em> the walk is
    /// asserted where it is observable, in
    /// <see cref="BoundedFileWalkTests.CarriesTheFormOfTheRootDownToEveryFileItVisits"/>.</para>
    /// </summary>
    [Fact]
    public void KeepsTheDisplayFormOfARootItWasGivenInExtendedForm()
    {
        var root = _temp.CreateDirectory("cache");
        var file = _temp.CreateFile(64, "cache", "content-v2", "sha512", "a.tgz");

        var tree = WalkExploreReader.Read(LongPath.Extended(root), onLevel: null, default);
        var deepest = ByPath(tree)[file];

        Assert.Equal(root, tree.RootPath);
        Assert.Equal(file, tree.PathOf(deepest));
        Assert.DoesNotContain(@"\\?\", tree.PathOf(deepest), StringComparison.Ordinal);
        Assert.True(File.Exists(tree.PathOf(deepest)));
    }

    /// <summary>
    /// One report per breadth-first level with the running counts, which is the cadence §5.5 wants:
    /// coarse enough to be worth marshalling to a UI, frequent enough that a large scan does not
    /// look stalled. The counts have to rise, or the window shows a scan that is running and never
    /// getting anywhere.
    /// </summary>
    [Fact]
    public void ReportsRisingCountsOncePerLevel()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1000, "cache", "a.bin");
        _temp.CreateFile(2000, "cache", "one", "b.bin");
        _temp.CreateFile(4000, "cache", "one", "two", "c.bin");

        var reports = new List<(long Items, long Bytes)>();

        WalkExploreReader.Read(root, (_, items, bytes) => reports.Add((items, bytes)), default);

        Assert.True(reports.Count >= 3, $"Only {reports.Count} levels were reported.");
        Assert.Equal(reports.OrderBy(r => r.Items).ThenBy(r => r.Bytes), reports);
        Assert.Equal((5, 7000), reports[^1]);
    }

    /// <summary>
    /// The dates come off the directory entry the enumeration already read, which is what lets this
    /// route answer the same question the file table does without a second pass over the disk.
    ///
    /// <para>Stamped rather than taken from the clock, so the assertion is that these values
    /// arrived rather than that some value did. Two distinct instants, created older than written,
    /// so swapping the two fields fails rather than coinciding.</para>
    /// </summary>
    [Fact]
    public void DatesEveryEntryFromWhatTheEnumerationAlreadyRead()
    {
        var made = new DateTime(2021, 4, 5, 9, 15, 0, DateTimeKind.Utc);
        var written = new DateTime(2024, 11, 30, 17, 45, 0, DateTimeKind.Utc);

        var root = _temp.CreateDirectory("cache");
        var file = _temp.CreateFile(1024, "cache", "a.tgz");

        File.SetCreationTimeUtc(file, made);
        File.SetLastWriteTimeUtc(file, written);

        // Older than the file, and deliberately: creating an entry moves the containing directory's
        // own write time to now, which would make the roll-up below pass by reporting a date this
        // test never asked for. This is also the shape the roll-up exists for — a folder whose
        // layout was settled long before its contents were last rewritten.
        Directory.SetLastWriteTimeUtc(root, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var tree = WalkExploreReader.Read(root, onLevel: null, default);
        var byPath = ByPath(tree);

        var node = byPath[Path.Combine(root, "a.tgz")];

        Assert.Equal(made, tree.CreatedOf(node).Utc);
        Assert.Equal(written, tree.ModifiedOf(node).Utc);

        // The containing directory takes the file's write, which is the roll-up reaching the route
        // rather than only the tree that assembles it.
        Assert.Equal(written, tree.ModifiedOf(tree.RootNode).Utc);
    }

    /// <summary>
    /// The scan's own root is dated too, and it is the one entry nothing enumerated — so it is the
    /// one the reader has to look up itself. Without that its creation date is the unknown the
    /// arrays start at, whatever the disk says.
    /// </summary>
    [Fact]
    public void DatesTheRootItWasHandedAsWellAsWhatIsInside()
    {
        var made = new DateTime(2019, 2, 3, 8, 30, 0, DateTimeKind.Utc);

        var root = _temp.CreateDirectory("cache");
        Directory.SetCreationTimeUtc(root, made);

        var tree = WalkExploreReader.Read(root, onLevel: null, default);

        Assert.Equal(made, tree.CreatedOf(tree.RootNode).Utc);
    }

    /// <summary>
    /// A junction is dated by its own entry rather than by whatever it points at, for the same
    /// reason it is sized at zero: the target holds its own place in this tree and carries its own
    /// dates there. Dating it from the target would report one instant twice and say nothing about
    /// the link itself.
    /// </summary>
    [Fact]
    public void DatesALinkByItsOwnEntryRatherThanItsTarget()
    {
        var made = new DateTime(2020, 7, 7, 11, 0, 0, DateTimeKind.Utc);

        var root = _temp.CreateDirectory("cache");
        var target = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(2048, "elsewhere", "big.bin");

        var link = Path.Combine(root, "shortcut");
        Directory.CreateSymbolicLink(link, target);
        Directory.SetCreationTimeUtc(link, made);

        var tree = WalkExploreReader.Read(root, onLevel: null, default);
        var node = ByPath(tree)[link];

        Assert.True(tree.IsLink(node));
        Assert.Equal(made, tree.CreatedOf(node).Utc);
    }

    private static int CountNamed(ExploreTree tree, string name)
    {
        var count = 0;

        for (var node = 0; node < tree.NodeCount; node++)
        {
            if (tree.NameOf(node) == name)
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> Paths(string root, params string[] relative) =>
        relative.Select(r => Path.Combine(root, r)).Order(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, int> ByPath(ExploreTree tree)
    {
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var node = 0; node < tree.NodeCount; node++)
        {
            byPath.Add(tree.PathOf(node), node);
        }

        return byPath;
    }
}
