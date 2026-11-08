using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A listing that lends its names (<see cref="FileNames.AllLent"/>) rather than building a string for
/// each: it names every entry as a listing with strings would, builds no string to do it, and refuses
/// to read a name once the listing that lent it is over.
/// </summary>
public sealed class LentNamesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void NamesEveryEntryAsAListingWithStringsDoes()
    {
        var folder = LongPath.Extended(Fixture(files: 40));

        var withStrings = Listed(folder, FileNames.All);
        var lent = Listed(folder, FileNames.AllLent);

        Assert.Equal(withStrings, lent);
    }

    /// <summary>
    /// §6.3: a lent name is joined to the directory's extended form, so the path a caller descends into
    /// or measures keeps the prefix.
    /// </summary>
    [Fact]
    public void BuildsEachPathInTheExtendedForm()
    {
        var folder = LongPath.Extended(Fixture(files: 2));

        using var listing = Listing(FileNames.AllLent);
        var contents = listing.Read(folder, followsLink: true);

        Assert.All(contents.Entries, entry =>
        {
            Assert.StartsWith(@"\\?\", entry.FullName, StringComparison.Ordinal);
            Assert.Equal(folder + @"\" + entry.NameSpan.ToString(), entry.FullName);
        });
    }

    /// <summary>
    /// A name kept past its listing would otherwise read as whatever the next listing lent at its
    /// place: another file's name, and in code that deletes paths, another file's path.
    /// </summary>
    [Fact]
    public void ANameKeptPastItsListingCannotBeRead()
    {
        var first = LongPath.Extended(Fixture(files: 3, name: "first"));
        var second = LongPath.Extended(Fixture(files: 3, name: "second"));

        using var listing = Listing(FileNames.AllLent);
        var kept = listing.Read(first, followsLink: true).Entries[0];
        var name = kept.Name;

        Assert.Equal(name, listing.Read(first, followsLink: true).Entries[0].Name);

        listing.Read(second, followsLink: true);

        Assert.Throws<InvalidOperationException>(() => kept.Name);
        Assert.Throws<InvalidOperationException>(() => kept.FullName);
    }

    /// <summary>
    /// The point of lending: a listing of a folder of files builds no string for any of them, where a
    /// listing with strings builds one each. The two listings differ only in how they keep names, so
    /// what lending saves has to be every string: at least twenty bytes and two for each character,
    /// for every entry. And lending allocates nothing for any entry at all, whether or not the JIT has
    /// optimised the path yet: an <see cref="Enum.HasFlag"/> boxes until it has, so one left on the
    /// path allocates for every entry, and in a suite run in parallel the two listings can be
    /// measured at different tiers.
    /// </summary>
    [Fact]
    public void ListsAndDescribesAFolderWithoutAStringForEachName()
    {
        var folder = LongPath.Extended(Fixture(files: 2_000));
        var names = Directory.EnumerateFileSystemEntries(folder).Select(path => Path.GetFileName(path)).ToList();
        var strings = names.Sum(name => 20L + (2L * name.Length));

        var lent = BytesToListAndDescribe(folder, FileNames.AllLent);
        var withStrings = BytesToListAndDescribe(folder, FileNames.All);

        Assert.True(
            withStrings - lent >= strings,
            $"Lending saved {withStrings - lent} bytes of the {strings} the names' strings take ({lent} lent, {withStrings} as strings).");
        Assert.True(
            lent < names.Count,
            $"Listing and describing {names.Count} entries with lent names allocated {lent} bytes, so something allocates for each entry.");
    }

    /// <summary>
    /// Names that fill the lent memory many times over, the longest a listing can give among them,
    /// each read back whole, and again after a recall has let the memory past the first chunks go.
    /// </summary>
    [Fact]
    public void LendsEachNameWholeAcrossChunks()
    {
        var names = new ListingNames();
        var expected = Enumerable.Range(0, 3_000).Select(i => new string((char)('a' + (i % 26)), 100 + (i % 156))).ToList();
        var longest = new string('z', 32 * 1024);

        for (var pass = 0; pass < 2; pass++)
        {
            var lent = expected.Select(name => names.Lend(name)).ToList();
            var lentLongest = names.Lend(longest);

            Assert.Equal(expected, lent.Select(name => name.ToString()));
            Assert.Equal(longest, lentLongest.ToString());

            names.Recall();

            Assert.Throws<InvalidOperationException>(() => lent[0].ToString());
            Assert.Throws<InvalidOperationException>(() => lent[^1].ToString());
        }
    }

    /// <summary>
    /// The Explore walk's tree from a listing that lends its names is the tree the same listing gives
    /// with a string for each: node for node, with the same names, parents, sizes and order. The tree
    /// is listed rather than read from a disk so that it can hold folders of thousands of entries,
    /// which fill the lent memory many times over.
    /// </summary>
    [Fact]
    public void TheExploreWalkBuildsTheSameTreeFromLentNames()
    {
        var root = LongPath.Extended(_temp.CreateDirectory("tree"));
        var listing = new SyntheticListing(root);
        var lends = false;

        ListDirectory lending = (directory, request, into) =>
        {
            lends |= request.Files.Lends;
            return listing.List(directory, request, into);
        };

        ListDirectory withStrings = (directory, request, into) =>
            listing.List(directory, request with { Files = FileNames.All }, into);

        var fromLent = Walk(root, lending);
        var fromStrings = Walk(root, withStrings);

        Assert.True(lends, "The Explore walk did not ask its listings to lend their names.");
        Assert.Equal(listing.EntryCount + 1, fromLent.NodeCount);
        AssertSameTree(fromStrings, fromLent);
    }

    /// <summary>
    /// The builder ranks and pools a batch of lent names as it does the same names as strings, so a
    /// snapshot in name order places every child alike.
    /// </summary>
    [Fact]
    public void TheBuilderRanksAndPoolsLentNamesAsItDoesStrings()
    {
        string[] names = ["beta", "Alpha", "alpha", "gamma.txt", "Beta", "delta"];
        var memory = new ListingNames();

        var lent = Build([.. names.Select(name => new ExploreChild(memory.Lend(name), IsDirectory: false, IsLink: false, Size: 1))]);
        var owned = Build([.. names.Select(name => new ExploreChild(name, IsDirectory: false, IsLink: false, Size: 1))]);

        AssertSameTree(owned, lent);

        static ExploreTree Build(ExploreChild[] children)
        {
            var builder = new ExploreTreeBuilder(@"C:\Users\testuser\folder");
            builder.AddChildren(ExploreTreeBuilder.RootNode, children);
            return builder.Build(ExploreChildOrder.ByName);
        }
    }

    private static ExploreTree Walk(string root, ListDirectory list) =>
        WalkExploreReader.Read(
            root,
            new WalkTuning(threads: 1, WalkTuning.MinimumListingBuffer),
            list,
            OccupancyProbe.Default,
            progress: null,
            TimeProvider.System,
            default);

    private static void AssertSameTree(ExploreTree expected, ExploreTree actual)
    {
        Assert.Equal(expected.NodeCount, actual.NodeCount);

        for (var node = 0; node < expected.NodeCount; node++)
        {
            Assert.Equal(expected.NameOf(node), actual.NameOf(node));
            Assert.Equal(expected.ParentOf(node), actual.ParentOf(node));
            Assert.Equal(expected.SizeOf(node), actual.SizeOf(node));
            Assert.Equal(expected.IsDirectory(node), actual.IsDirectory(node));
            Assert.Equal(expected.ChildrenOf(node).ToArray(), actual.ChildrenOf(node).ToArray());
        }
    }

    private static List<(string Name, string FullName, bool IsDirectory, long Length)> Listed(string folder, FileNames names)
    {
        using var listing = Listing(names);
        var contents = listing.Read(folder, followsLink: true);

        return [.. contents.Entries
            .Select(entry => (entry.Name, entry.FullName, entry.IsDirectory, entry.Length))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)];
    }

    private static long BytesToListAndDescribe(string folder, FileNames names)
    {
        using var listing = Listing(names);
        var children = new ExploreChild[4_096];

        // Once to grow the lists and the lent memory, which a worker does once for a whole walk.
        Describe(listing.Read(folder, followsLink: true), children);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Describe(listing.Read(folder, followsLink: true), children);
        return GC.GetAllocatedBytesForCurrentThread() - before;

        static void Describe(DirectoryContents contents, ExploreChild[] into) =>
            WalkExploreReader.Describe(contents, OccupancyProbe.Default, into.AsSpan(0, contents.Count));
    }

    private static WalkListing Listing(FileNames names) =>
        new(new ListingBuffer(WalkTuning.MinimumListingBuffer), DirectoryQuery.List, names);

    private string Fixture(int files, string name = "folder")
    {
        var folder = _temp.CreateDirectory(name);
        _temp.CreateDirectory(name, "Sub Folder");

        for (var i = 0; i < files; i++)
        {
            _temp.CreateFile(i % 7, name, $"file-{i:D5}-of-a-listing.bin");
        }

        return folder;
    }

    /// <summary>
    /// A tree no volume holds: a root of three folders, each of 3,000 files and a folder of 1,500, with
    /// names of up to 200 characters that differ in case and repeat from folder to folder. Each listing
    /// keeps its names as the request asks, as one from the disk does.
    /// </summary>
    private sealed class SyntheticListing(string root)
    {
        private readonly Dictionary<string, (string Name, bool IsDirectory, long Length)[]> _folders =
            Build(root);

        public int EntryCount => _folders.Values.Sum(entries => entries.Length);

        public ListingEnd List(string directory, ListingRequest request, List<WalkEntry> into)
        {
            foreach (var (name, isDirectory, length) in _folders.GetValueOrDefault(directory, []))
            {
                into.Add(new WalkEntry(
                    directory,
                    request.NameOf(name, isDirectory),
                    isDirectory ? FileAttributes.Directory : FileAttributes.Archive,
                    length,
                    DateTime.UnixEpoch,
                    DateTime.UnixEpoch));
            }

            return ListingEnd.Listed;
        }

        private static Dictionary<string, (string, bool, long)[]> Build(string root)
        {
            var folders = new Dictionary<string, (string, bool, long)[]>(StringComparer.OrdinalIgnoreCase);
            string[] top = ["Alpha", "beta", "GAMMA"];

            folders[root] = [.. top.Select(name => (name, true, 0L))];

            foreach (var name in top)
            {
                var folder = Path.Join(root, name);
                folders[folder] = [.. Files(3_000), ("inner", true, 0L)];
                folders[Path.Join(folder, "inner")] = Files(1_500);
            }

            return folders;

            // Sizes repeat, so siblings of one size are put in order by their names.
            static (string, bool, long)[] Files(int count) =>
                [.. Enumerable.Range(0, count).Select(i =>
                    ((i % 2 == 0 ? "File-" : "file-") + i.ToString("D5") + new string('x', i % 190), false, (long)(i % 5)))];
        }
    }
}
