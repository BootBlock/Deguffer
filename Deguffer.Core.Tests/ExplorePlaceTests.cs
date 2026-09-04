using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Core.Tests.Fakes;

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
    private const uint Cache = 8;

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
    /// The file-table route, which is the case the issue behind these tests called out. It publishes
    /// no snapshot and numbers its nodes by record, so a node held from a walked scan lands on a
    /// record that is in range and describes nothing at all. Placing it is refused rather than
    /// guessed. <c>MftExploreReaderTests</c> is where that refusal is pinned against the shape a
    /// real volume leaves, which this fixture's blank metadata records do not reproduce.
    /// </summary>
    [Fact]
    public void GoesBackToTheRootWhenTheNodeIsUnreachableInTheNewTree()
    {
        using var source = new MftFixture()
            .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
            .AddDirectory(ProfileRecord, Users, "testuser")
            .AddDirectory(Cache, ProfileRecord, ".npm-cache")
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .Build();

        var table = MftExploreReader.Read(source, @"C:\", [], onProgress: null, default).Tree!;
        var walked = ProfileTree(@"C:\");

        // Record 1 is in range and was never described, so this tree cannot place it.
        Assert.Null(table.TryPathOf(1));
        Assert.Equal(table.RootNode, ExplorePlace.Carry(walked, 1, table));
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
