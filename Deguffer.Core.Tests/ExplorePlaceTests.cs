using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where the Explore page stands once the tree under it is replaced, which is what decides whether
/// a user can read a folder while a scan is still running.
///
/// <para>Worth its own tests because both answers are silent. Losing the place puts somebody back
/// at the drive root every time a snapshot lands, and keeping a node number that no longer names
/// what it did shows one folder's contents under another folder's name — and neither raises
/// anything for a caller to notice.</para>
/// </summary>
public class ExplorePlaceTests
{
    private const string Root = @"C:\Users\testuser";

    // A synthetic profile tree for the file-table half, with invented paths.
    private const uint Users = 6;
    private const uint ProfileRecord = 7;
    private const uint Android = 8;
    private const uint Cache = 9;

    private const string CachePath = @"C:\Users\testuser\.npm-cache";

    /// <summary>
    /// The case the page exists to fix: the walk publishes a partial tree every so often, and the
    /// node the user descended into is the same node in the next one.
    /// </summary>
    [Fact]
    public void KeepsThePlaceAcrossTheSnapshotsOfOneWalk()
    {
        var builder = new ExploreTreeBuilder(Root);

        var cache = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            Folder("npm-cache"),
            Folder("nuget"),
        ]);

        var snapshot = builder.Build(ExploreChildOrder.ByName);

        // The walk carries on below and beside where the user is standing, which is what makes the
        // next snapshot a different tree rather than the same one.
        builder.AddChildren(cache, [Entry("a.tgz", 4096)]);
        builder.AddChildren(cache + 1, [Entry("b.nupkg", 8192)]);

        var later = builder.Build(ExploreChildOrder.ByName);

        Assert.Equal(cache, ExplorePlace.Carry(snapshot, cache, later));
    }

    /// <summary>
    /// And into the finished tree, which is the last of those snapshots and the one whose arrival
    /// would otherwise throw the place away just as the scan became useful.
    /// </summary>
    [Fact]
    public void KeepsThePlaceWhenTheWalkFinishes()
    {
        var builder = new ExploreTreeBuilder(Root);

        var cache = builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder("npm-cache")]);
        var snapshot = builder.Build(ExploreChildOrder.ByName);

        builder.AddChildren(cache, [Entry("a.tgz", 4096)]);

        // The finished tree orders children by size rather than by name, and the numbering is
        // unaffected by that — the sort moves child lists, not nodes.
        var finished = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(cache, ExplorePlace.Carry(snapshot, cache, finished));
    }

    /// <summary>
    /// The same volume written differently is the same volume. Two scans of one drive can be rooted
    /// at <c>C:\</c> and <c>c:\</c>, and NTFS does not tell those apart either.
    /// </summary>
    [Fact]
    public void KeepsThePlaceWhenOnlyTheCaseOfTheRootDiffers()
    {
        var before = ProfileTree(@"C:\Users\testuser");
        var after = ProfileTree(@"c:\users\testuser");

        Assert.Equal(1, ExplorePlace.Carry(before, 1, after));
    }

    /// <summary>
    /// The dangerous direction, and the reason the rule compares paths rather than trusting node
    /// numbers. Two scans of different drives number their nodes identically, so a node kept on its
    /// number alone would leave the page naming one directory while listing another's contents.
    /// </summary>
    [Fact]
    public void GoesBackToTheRootWhenTheNodeNamesSomethingElse()
    {
        var before = ProfileTree(@"C:\Users\testuser");
        var after = ProfileTree(@"D:\Users\testuser");

        Assert.Equal(after.RootNode, ExplorePlace.Carry(before, 1, after));
    }

    /// <summary>
    /// A node past the end of the tree that replaces it. A second scan can find less than the first
    /// did — a folder scan after a whole-drive one, or a drive whose contents have gone — and the
    /// range is the question rather than a precondition on it.
    /// </summary>
    [Fact]
    public void GoesBackToTheRootWhenTheNewTreeHasNoSuchNode()
    {
        var before = ProfileTree(Root);

        var small = new ExploreTreeBuilder(Root);
        small.AddChildren(ExploreTreeBuilder.RootNode, [Entry("a.tgz", 4096)]);

        Assert.Equal(
            ExploreTreeBuilder.RootNode,
            ExplorePlace.Carry(before, before.NodeCount - 1, small.Build(ExploreChildOrder.BySize)));
    }

    /// <summary>
    /// A node held from a walked scan whose path the file table's tree of the same place does not
    /// hold. The number is in range there and names something else, so it is not trusted, and the
    /// page goes back to the root.
    /// </summary>
    [Fact]
    public void GoesBackToTheRootWhenTheNewTreeHoldsNothingAtThatPath()
    {
        using var source = Table().Build();

        var table = WholeVolume(source);
        var walked = ProfileTree(@"C:\");

        Assert.True(table.Holds(1));
        Assert.Equal(table.RootNode, ExplorePlace.Carry(walked, 1, table));
    }

    /// <summary>
    /// #364: the file table's tree keeps only what its root reaches and numbers that again, so two
    /// scans of one volume can give one folder two numbers. Here a folder made between them takes a
    /// record before the cache's, which moves the cache's number, and the place follows the cache to
    /// it. Trusting the number would stand the page on <c>.android</c> under the cache's name.
    /// </summary>
    [Fact]
    public void KeepsThePlaceAcrossTwoTableScansThatNumberItDifferently()
    {
        using var before = Table().Build();
        using var after = Table().AddDirectory(Android, ProfileRecord, ".android").Build();

        var standing = WholeVolume(before);
        var arriving = WholeVolume(after);
        var cache = ExploreTreePaths.NodeAt(standing, CachePath);
        var moved = ExploreTreePaths.NodeAt(arriving, CachePath);

        // The case under test: the same folder under another number.
        Assert.NotEqual(cache, moved);

        Assert.Equal(moved, ExplorePlace.Carry(standing, cache, arriving));
        Assert.Equal(moved, ExplorePlace.TryCarry(standing, cache, arriving));
    }

    /// <summary>
    /// A walked scan and a file-table scan of one place number their nodes by different rules, and
    /// the place still carries between them, because it is found by its path.
    /// </summary>
    [Fact]
    public void KeepsThePlaceFromAWalkIntoATableScanOfTheSamePlace()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var users = builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder("Users")]);
        var profile = builder.AddChildren(users, [Folder("testuser")]);
        var cache = builder.AddChildren(profile, [Folder(".npm-cache")]);
        var walked = builder.Build(ExploreChildOrder.BySize);

        using var source = Table().Build();
        var table = WholeVolume(source);

        Assert.Equal(ExploreTreePaths.NodeAt(table, CachePath), ExplorePlace.Carry(walked, cache, table));
    }

    /// <summary>
    /// A directory with case sensitivity turned on holds <c>Cache</c> and <c>cache</c> as two folders.
    /// A rescan that numbers them the other way round gives <c>cache</c> the number <c>Cache</c> had,
    /// and the place follows the name spelt exactly, not the number.
    /// </summary>
    [Fact]
    public void CarriesAFolderToItsOwnSpellingWhereTwoDifferOnlyInCase()
    {
        var first = new ExploreTreeBuilder(Root);
        var upper = first.AddChildren(ExploreTreeBuilder.RootNode, [Folder("Cache"), Folder("cache")]);
        var standing = first.Build(ExploreChildOrder.BySize);

        var second = new ExploreTreeBuilder(Root);
        var lower = second.AddChildren(ExploreTreeBuilder.RootNode, [Folder("cache"), Folder("Cache")]);
        var arriving = second.Build(ExploreChildOrder.BySize);

        // The case under test: the number Cache had is cache's now.
        Assert.Equal("cache", arriving.NameOf(upper));

        Assert.Equal(lower + 1, ExplorePlace.TryCarry(standing, upper, arriving));
        Assert.Equal(lower, ExplorePlace.TryCarry(standing, upper + 1, arriving));
    }

    /// <summary>Nothing on screen yet, which is every first scan.</summary>
    [Fact]
    public void GoesToTheRootWhenNothingWasBeingShown()
    {
        var arriving = ProfileTree(Root);

        Assert.Equal(arriving.RootNode, ExplorePlace.Carry(standing: null, 3, arriving));
    }

    /// <summary>
    /// The selection's rule, and the whole of what separates it from the page's. A node the user
    /// picked out by hand is kept only where it still names what it named, and answering the root
    /// instead — which is what the page wants — would select a drive nobody asked to select.
    /// </summary>
    [Fact]
    public void CarriesNothingWhenTheNodeNamesSomethingElse()
    {
        var before = ProfileTree(@"C:\Users\testuser");
        var after = ProfileTree(@"D:\Users\testuser");

        Assert.Null(ExplorePlace.TryCarry(before, 1, after));
    }

    /// <summary>
    /// The case a selection has to survive: the walk publishes a partial tree every so often, and
    /// what was picked out of one is the same thing in the next.
    /// </summary>
    [Fact]
    public void CarriesTheNodeAcrossTheSnapshotsOfOneWalk()
    {
        var builder = new ExploreTreeBuilder(Root);

        var cache = builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder("npm-cache")]);
        var snapshot = builder.Build(ExploreChildOrder.ByName);

        builder.AddChildren(cache, [Entry("a.tgz", 4096)]);

        Assert.Equal(cache, ExplorePlace.TryCarry(snapshot, cache, builder.Build(ExploreChildOrder.BySize)));
    }

    /// <summary>Nothing on screen to have picked anything out of.</summary>
    [Fact]
    public void CarriesNothingWhenNothingWasBeingShown()
    {
        Assert.Null(ExplorePlace.TryCarry(standing: null, 3, ProfileTree(Root)));
    }

    /// <summary>
    /// §7.1: Explore acts only on what was picked by hand. Where <c>Cache</c> has gone, from a
    /// removal or a rescan, nothing that stood in it carries to <c>cache</c> beside it, a different
    /// folder whose name differs only in case, or to anything below <c>cache</c>.
    /// </summary>
    [Fact]
    public void CarriesNothingToAFolderWhoseNameDiffersOnlyInCase()
    {
        var first = new ExploreTreeBuilder(Root);
        var upper = first.AddChildren(ExploreTreeBuilder.RootNode, [Folder("Cache"), Folder("cache")]);
        var inUpper = first.AddChildren(upper, [Entry("big.bin", 4096)]);
        first.AddChildren(upper + 1, [Entry("big.bin", 4096)]);
        var standing = first.Build(ExploreChildOrder.BySize);

        var left = standing.Without([upper]);

        var second = new ExploreTreeBuilder(Root);
        var lower = second.AddChildren(ExploreTreeBuilder.RootNode, [Folder("cache")]);
        second.AddChildren(lower, [Entry("big.bin", 4096)]);
        var rescan = second.Build(ExploreChildOrder.BySize);

        Assert.Null(ExplorePlace.TryCarry(standing, upper, left));
        Assert.Null(ExplorePlace.TryCarry(standing, inUpper, left));
        Assert.Null(ExplorePlace.TryCarry(standing, upper, rescan));
        Assert.Null(ExplorePlace.TryCarry(standing, inUpper, rescan));

        // The folder that is still there carries, so the refusal is of the spelling alone.
        Assert.Equal(lower, ExplorePlace.TryCarry(standing, upper + 1, rescan));
    }

    /// <summary>A file table holding a profile's cache, with invented paths.</summary>
    private static MftFixture Table() => new MftFixture()
        .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(ProfileRecord, Users, "testuser")
        .AddDirectory(Cache, ProfileRecord, ".npm-cache")
        .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096);

    private static ExploreTree WholeVolume(IMftSource source) =>
        MftExploreReader.Read(source, @"C:\", [], TableTuning.Default, onProgress: null, default).Tree!;

    /// <summary>A whole small tree, built the way the walk builds one.</summary>
    private static ExploreTree ProfileTree(string root)
    {
        var builder = new ExploreTreeBuilder(root);

        var first = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            Folder("npm-cache"),
            Folder("nuget"),
        ]);

        builder.AddChildren(first, [Entry("a.tgz", 4096)]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static ExploreChild Folder(string name) =>
        new(name, IsDirectory: true, IsLink: false, Size: 0);

    private static ExploreChild Entry(string name, long size) =>
        new(name, IsDirectory: false, IsLink: false, Size: size);
}
