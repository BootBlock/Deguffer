using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a listing keeps of the files it meets (<see cref="FileNames"/>), and the two rules that make a
/// file listed without its name safe: it is never handed a name it does not have, and every store
/// among the files is named all the same (§9).
/// </summary>
public sealed class WalkFileNamesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A walk that totals sizes still reads every file's length, and names only the store. A store
    /// listed without its name would read as an ordinary file, counted and handed to a removal.
    /// </summary>
    [Fact]
    public void NamesOnlyTheStoresAndTheDirectoriesWhenAskedForStores()
    {
        var folder = Fixture();

        var listed = List(folder, FileNames.Stores);

        Assert.Equal(["inbox.ost", "sub"], listed.Where(e => e.HasName).Select(e => e.Name).Order());
        Assert.Equal(3, listed.Count(e => !e.IsDirectory));

        var unnamed = listed.Where(e => !e.HasName).ToList();
        Assert.Equal(2, unnamed.Count);
        Assert.Contains(unnamed, e => e.Length == 1234);
        Assert.All(unnamed, e => Assert.False(e.IsMailStore));
        Assert.True(Assert.Single(listed, e => e.HasName && !e.IsDirectory).IsMailStore);
    }

    /// <summary>The Chromium walk's marker is named beside the stores, and nothing else is.</summary>
    [Fact]
    public void NamesTheOneFileAskedForBesideTheStores()
    {
        var folder = Fixture();

        var listed = List(folder, FileNames.StoresAnd("LOCAL STATE"));

        Assert.Equal(["inbox.ost", "Local State", "sub"], listed.Where(e => e.HasName).Select(e => e.Name).Order());
    }

    [Fact]
    public void LeavesFilesOutWhenAskedForNone()
    {
        var folder = Fixture();

        var listed = List(folder, FileNames.None);

        Assert.Equal(["sub"], listed.Select(e => e.Name));
    }

    [Fact]
    public void NamesEveryFileWhenAskedForAll()
    {
        var folder = Fixture();

        var listed = List(folder, FileNames.All);

        Assert.Equal(["data.bin", "inbox.ost", "Local State", "sub"], listed.Select(e => e.Name).Order());
    }

    /// <summary>
    /// A nameless file's path is never its folder's. Deleting by a <see cref="WalkEntry.FullName"/> that
    /// came out as the parent directory would take the directory, so asking throws instead.
    /// </summary>
    [Fact]
    public void AFileListedWithoutItsNameHasNoPath()
    {
        var folder = Fixture();

        var file = List(folder, FileNames.Stores).First(e => !e.HasName);

        Assert.Throws<InvalidOperationException>(() => file.Name);
        Assert.Throws<InvalidOperationException>(() => file.FullName);

        // A failing assertion prints the entry, and must not throw while it does.
        Assert.Contains("(not kept)", file.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesADirectoryWithoutAName() =>
        Assert.Throws<ArgumentException>(() => new WalkEntry(
            @"\\?\C:\folder", name: null, FileAttributes.Directory, 0, DateTime.UnixEpoch, DateTime.UnixEpoch));

    /// <summary>
    /// The listing says which directory it describes, as the string its entries share, so a caller
    /// needs no copy of the path of its own.
    /// </summary>
    [Fact]
    public void TellsTheCallerWhichDirectoryItListed()
    {
        var folder = LongPath.Extended(Fixture());

        using var listing = new WalkListing(
            new ListingBuffer(WalkTuning.MinimumListingBuffer), DirectoryQuery.List, FileNames.Stores);
        var contents = listing.Read(folder, followsLink: true);

        Assert.Same(folder, contents.Directory);
        Assert.All(contents.Entries, entry => Assert.Same(folder, entry.Directory));
    }

    /// <summary>
    /// A folder holding an entry some root leaves out is listed with every name, whatever the walk asked
    /// for, because the left-out names are compared with its files'. Any other folder keeps the walk's
    /// choice, which is what spares a large tree its names.
    /// </summary>
    [Fact]
    public void NamesEveryFileInAFolderHoldingAnEntryLeftOut()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(10, "cache", "plain.bin");
        _temp.CreateFile(20, "cache", "holder", "spared.bin");
        _temp.CreateFile(30, "cache", "holder", "counted.bin");

        var named = new System.Collections.Concurrent.ConcurrentDictionary<string, string[]>();
        var left = new System.Collections.Concurrent.ConcurrentDictionary<string, string[]>();

        RootSetWalk.Visit<string>(
            [WalkRoot.Of(root, new ScanBounds(KeepRoot: false, LeftOut: [Path.Combine(root, "holder", "spared.bin")]))],
            new WalkTuning(1, WalkTuning.MinimumListingBuffer),
            FileNames.Stores,
            (_, _, contents, leftOut) =>
            {
                var folder = Path.GetFileName(contents.Directory);
                named[folder] = [.. contents.Entries.Where(e => !e.IsDirectory && e.HasName).Select(e => e.Name).Order()];
                left[folder] = [.. leftOut ?? new HashSet<string>()];
                return folder;
            },
            static () => { },
            TimeProvider.System,
            default);

        Assert.Equal(["counted.bin", "spared.bin"], named["holder"]);
        Assert.Equal(["spared.bin"], left["holder"]);
        Assert.Empty(named["cache"]);
    }

    /// <summary>
    /// A folder of three files and one folder: an ordinary file, a store, and a file called as the
    /// Chromium marker is.
    /// </summary>
    private string Fixture()
    {
        var folder = _temp.CreateDirectory("folder");
        _temp.CreateFile(1234, "folder", "data.bin");
        _temp.CreateFile(10, "folder", "inbox.ost");
        _temp.CreateFile(10, "folder", "Local State");
        _temp.CreateDirectory("folder", "sub");
        return folder;
    }

    private static List<WalkEntry> List(string folder, FileNames files)
    {
        var into = new List<WalkEntry>();
        using var memory = new ListingMemory(WalkTuning.MinimumListingBuffer);
        var end = DirectoryQuery.List(LongPath.Extended(folder), new ListingRequest(memory, memory.Bytes, FollowsLink: true, files), into);
        Assert.Equal(PathPresence.Present, end.Presence);
        return into;
    }
}
