using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

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

        var tree = WalkExploreReader.Read(root, ScanTuner.Shipped, OccupancyProbe.Default, onProgress: null, TimeProvider.System, default);
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

        var tree = WalkExploreReader.Read(root, ScanTuner.Shipped, OccupancyProbe.Default, onProgress: null, TimeProvider.System, default);
        var byPath = ByPath(tree);

        Assert.Equal(4608, tree.TotalBytes);
        Assert.True(tree.HasUnknownSizes);
        Assert.True(tree.HasUnknownSizeBelow(byPath[refused]));
        Assert.True(tree.HasUnknownSizeBelow(byPath[Path.Combine(root, "content-v2")]));
        Assert.False(tree.HasUnknownSizeBelow(byPath[Path.Combine(root, "logs")]));
    }

    /// <summary>
    /// The refusal the test above counts is reported, not thrown. A whole volume holds hundreds of
    /// folders an unelevated account may not list, and a walk that raised and caught one exception
    /// for each filled a debugger's output with them while giving the same answer.
    /// </summary>
    [Fact]
    public void WalksPastARefusedDirectoryWithoutThrowing()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "readable.bin");
        var refused = _temp.CreateDirectory("cache", "refused");
        _temp.CreateFile(65536, "cache", "refused", "unreachable.bin");

        using var denied = new DeniedDirectory(refused);

        ExploreTree? tree = null;
        var thrown = ThrownExceptions.During(() => tree = WalkExploreReader.Read(root, ScanTuner.Shipped, OccupancyProbe.Default, onProgress: null, TimeProvider.System, default));

        Assert.Empty(thrown);
        Assert.Equal(4096, tree!.TotalBytes);
        Assert.True(tree.HasUnknownSizeBelow(ByPath(tree)[refused]));
    }

    /// <summary>
    /// A directory link of either kind is shown and holds nothing. Its target keeps its own place in
    /// the tree, so counting through one would report the same bytes twice and draw a subtree the
    /// walk never classified — while hiding it altogether makes a directory the user can plainly see
    /// in Explorer vanish from the picture.
    ///
    /// <para>Both of those are asserted, because each alone passes for the wrong reason: a reader
    /// that dropped links entirely would satisfy "nothing appears twice", and one that followed them
    /// would satisfy "the link is present".</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void ShowsALinkAsEmptyAndNeverCountsItsTargetTwice(DirectoryLinkKind kind)
    {
        var root = _temp.CreateDirectory("cache");
        var real = _temp.CreateDirectory("cache", "content-v2");
        _temp.CreateFile(2048, "cache", "content-v2", "inside.bin");

        DirectoryLink.Create(kind, Path.Combine(root, "shortcut"), real);

        var tree = WalkExploreReader.Read(root, ScanTuner.Shipped, OccupancyProbe.Default, onProgress: null, TimeProvider.System, default);
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
    /// deep-tree test that cannot fail. The prefix on every path the walk hands back is asserted
    /// where it is observable, in
    /// <see cref="BoundedFileWalkTests.HandsBackEveryFileInTheExtendedLengthFormWhicheverFormTheRootHad"/>.</para>
    /// </summary>
    [Fact]
    public void KeepsTheDisplayFormOfARootItWasGivenInExtendedForm()
    {
        var root = _temp.CreateDirectory("cache");
        var file = _temp.CreateFile(64, "cache", "content-v2", "sha512", "a.tgz");

        var tree = WalkExploreReader.Read(LongPath.Extended(root), ScanTuner.Shipped, OccupancyProbe.Default, onProgress: null, TimeProvider.System, default);
        var deepest = ByPath(tree)[file];

        Assert.Equal(root, tree.RootPath);
        Assert.Equal(file, tree.PathOf(deepest));
        Assert.DoesNotContain(@"\\?\", tree.PathOf(deepest), StringComparison.Ordinal);
        Assert.True(File.Exists(tree.PathOf(deepest)));
    }

    /// <summary>
    /// A report with the running counts once per interval of the clock it is given, which is the
    /// cadence §5.5 wants: coarse enough to be worth marshalling to a UI, frequent enough that a
    /// large scan does not look stalled. The counts have to rise, or the window shows a scan that is
    /// running and never getting anywhere.
    ///
    /// <para>Each report moves the clock on by an interval, so every directory read is followed by
    /// one, and the counts are seen as the tree grows rather than only at the end.</para>
    /// </summary>
    [Fact]
    public void ReportsRisingCountsOncePerInterval()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1000, "cache", "a.bin");
        _temp.CreateFile(2000, "cache", "one", "b.bin");
        _temp.CreateFile(4000, "cache", "one", "two", "c.bin");

        var clock = new ManualTimeProvider();
        var reports = new List<(long Items, long Bytes)>();

        WalkExploreReader.Read(
            root, ScanTuner.Shipped, OccupancyProbe.Default,
            (_, items, bytes) =>
            {
                reports.Add((items, bytes));
                clock.Advance(BoundedFileWalk.ProgressInterval);
            },
            clock,
            default);

        Assert.True(reports.Count >= 3, $"Only {reports.Count} reports were made.");
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

        var tree = WalkExploreReader.Read(root, ScanTuner.Shipped, OccupancyProbe.Default, onProgress: null, TimeProvider.System, default);
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

        var tree = WalkExploreReader.Read(root, ScanTuner.Shipped, OccupancyProbe.Default, onProgress: null, TimeProvider.System, default);

        Assert.Equal(made, tree.CreatedOf(tree.RootNode).Utc);
    }

    /// <summary>
    /// A link is dated by its own entry rather than by whatever it points at, for the same
    /// reason it is sized at zero: the target holds its own place in this tree and carries its own
    /// dates there. Dating it from the target would report one instant twice and say nothing about
    /// the link itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void DatesALinkByItsOwnEntryRatherThanItsTarget(DirectoryLinkKind kind)
    {
        var made = new DateTime(2020, 7, 7, 11, 0, 0, DateTimeKind.Utc);

        var root = _temp.CreateDirectory("cache");
        var target = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(2048, "elsewhere", "big.bin");

        var link = Path.Combine(root, "shortcut");
        DirectoryLink.Create(kind, link, target);
        Directory.SetCreationTimeUtc(link, made);

        var tree = WalkExploreReader.Read(root, ScanTuner.Shipped, OccupancyProbe.Default, onProgress: null, TimeProvider.System, default);
        var node = ByPath(tree)[link];

        Assert.True(tree.IsLink(node));
        Assert.Equal(made, tree.CreatedOf(node).Utc);
    }

    /// <summary>
    /// #257 against Windows itself: a placeholder held only online is listed at its full length,
    /// and the walk draws it at what it occupies, which is nothing, without fetching a byte of it.
    /// A copy kept on this PC counts in full.
    ///
    /// <para>A real sync root rather than a fake, because the shape under test is Windows' own:
    /// it disguises a placeholder as an ordinary file to every process but its sync app, and
    /// leaves only the recall attribute to say the content is elsewhere.</para>
    /// </summary>
    [Fact]
    public void DrawsAFileHeldOnlyOnlineAtWhatItOccupiesWithoutFetchingIt()
    {
        using var synced = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));
        synced.OnlineOnly("film.mkv", 5_000_000);
        synced.LocalCopy("kept.bin", 300_000);
        synced.PlainFile("plain.bin", 300_000);
        synced.Disconnect();

        var tree = WalkExploreReader.Read(synced.Path, ScanTuner.Shipped, OccupancyProbe.Default, onProgress: null, TimeProvider.System, default);
        var byPath = ByPath(tree);
        var film = byPath[synced.At("film.mkv")];

        Assert.Equal(0, tree.SizeOf(film));
        Assert.Equal(5_000_000, tree.LengthOf(film));
        Assert.Equal(FileStorage.CloudOnly, tree.StorageOf(film));
        Assert.False(tree.IsLink(film));

        Assert.Equal(300_000, tree.SizeOf(byPath[synced.At("kept.bin")]));
        Assert.Equal(600_000, tree.TotalBytes);
        Assert.Equal(5_600_000, tree.TotalLength);
        Assert.False(tree.HasUnknownSizes);
        Assert.Equal(0, synced.FetchRequests);
    }

    /// <summary>
    /// The walk's half of #257 through the listing seam: a file carrying a reparse point that is not
    /// a link is drawn at what it occupies, where the walk used to leave it out of the tree. A
    /// symbolic link to a file is drawn as a link holding nothing, as the file table draws it.
    /// </summary>
    [Fact]
    public void DrawsAReparsePointFileAtWhatItOccupiesAndAFileLinkAsALink()
    {
        var probe = new FakeOccupancyProbe().Occupying("deduplicated.vhdx", 4096).Linking("shortcut.txt");
        var contents = Listing(
            Entry("plain.tgz", FileAttributes.Archive, 1000),
            ReparseFile("deduplicated.vhdx", FileAttributes.SparseFile, 1_000_000),
            ReparseFile("shortcut.txt", FileAttributes.None, 0));

        var tree = Tree(contents, probe);

        var deduplicated = Named(tree, "deduplicated.vhdx");
        Assert.Equal(4096, tree.SizeOf(deduplicated));
        Assert.Equal(1_000_000, tree.LengthOf(deduplicated));
        Assert.Equal(FileStorage.Sparse, tree.StorageOf(deduplicated));
        Assert.False(tree.IsLink(deduplicated));

        var shortcut = Named(tree, "shortcut.txt");
        Assert.True(tree.IsLink(shortcut));
        Assert.False(tree.IsDirectory(shortcut));
        Assert.Equal(0, tree.SizeOf(shortcut));

        Assert.Equal(1000 + 4096, tree.TotalBytes);
        Assert.False(tree.HasUnknownSizes);
    }

    /// <summary>
    /// Only a file whose attributes say its length may not be what it occupies is measured. Every
    /// other one is taken at its length, which is what keeps the walk to one listing per directory.
    /// </summary>
    [Fact]
    public void MeasuresOnlyTheFilesItsListingCannotSize()
    {
        var probe = new FakeOccupancyProbe().Occupying("online.mkv", 0).Occupying("log.txt", 131_072);
        var contents = Listing(
            Entry("plain.tgz", FileAttributes.Archive, 1000),
            Entry("online.mkv", FileAttributes.Archive | (FileAttributes)0x0040_0000, 5_000_000),
            Entry("log.txt", FileAttributes.Compressed, 2_097_152));

        var tree = Tree(contents, probe);

        Assert.Equal([Path.Join(Listed, "online.mkv"), Path.Join(Listed, "log.txt")], probe.Asked);
        Assert.Equal(1000 + 131_072, tree.TotalBytes);
        Assert.Equal(1000 + 5_000_000 + 2_097_152, tree.TotalLength);
        Assert.Equal(FileStorage.CloudOnly | FileStorage.Compressed, tree.StorageOf(tree.RootNode));
    }

    /// <summary>
    /// A file Windows would not measure is drawn at nothing and the totals above it say they are
    /// lower bounds. Drawing it at its length would be the overcount the measurement is there to stop.
    /// </summary>
    [Fact]
    public void MarksAFileWindowsWouldNotMeasureAsALowerBound()
    {
        var contents = Listing(Entry("gone.mkv", FileAttributes.Archive | FileAttributes.Offline, 5_000_000));

        var tree = Tree(contents, new FakeOccupancyProbe());

        Assert.Equal(0, tree.TotalBytes);
        Assert.Equal(5_000_000, tree.TotalLength);
        Assert.True(tree.HasUnknownSizes);
    }

    /// <summary>The directory the synthesised listings were made in, in the extended form the walk lists in.</summary>
    private const string Listed = @"\\?\C:\cache";

    private static readonly DateTime Written = new(2026, 1, 2, 3, 4, 0, DateTimeKind.Utc);

    private static WalkEntry Entry(string name, FileAttributes attributes, long length) =>
        new(Listed, name, attributes, length, Written, Written);

    private static WalkEntry ReparseFile(string name, FileAttributes attributes, long length) =>
        Entry(name, attributes | FileAttributes.ReparsePoint, length);

    /// <summary>A directory's listing sorted as the walk sorts it, reparse-point files apart.</summary>
    private static DirectoryContents Listing(params WalkEntry[] entries) => new(
        [.. entries.Where(e => !e.IsReparsePoint)],
        [],
        [.. entries.Where(e => e.IsReparsePoint)],
        WasRefused: false);

    private static ExploreTree Tree(DirectoryContents contents, IOccupancyProbe probe)
    {
        var builder = new ExploreTreeBuilder(@"C:\cache");
        builder.AddChildren(ExploreTreeBuilder.RootNode, WalkExploreReader.Describe(contents, probe));

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static int Named(ExploreTree tree, string name)
    {
        foreach (var child in tree.ChildrenOf(tree.RootNode))
        {
            if (tree.NameOf(child) == name)
            {
                return child;
            }
        }

        throw new InvalidOperationException($"No child is named {name}.");
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
