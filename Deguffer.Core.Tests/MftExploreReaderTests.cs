using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.5's fast path, applied to a whole volume rather than to a handful of named locations.
///
/// <para>The reader shares its byte-level half with <see cref="MftVolumeIndexBuilder"/> and answers
/// to the opposite policy, so most of these tests are contrasts rather than measurements: the same
/// synthetic table is fed to both, and what separates them is asserted directly. Anything less would
/// let the picture's tolerance for a record it could not read migrate into the index whose numbers
/// decide deletions.</para>
/// </summary>
public class MftExploreReaderTests
{
    // A synthetic profile tree. Paths are invented rather than copied from a real machine.
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Cache = 8;
    private const uint Nested = 9;
    private const uint Sibling = 10;

    private const string Root = @"C:\";

    private static MftFixture Tree() => new MftFixture()
        .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(Profile, Users, "testuser")
        .AddDirectory(Cache, Profile, ".npm-cache")
        .AddDirectory(Nested, Cache, "content-v2")
        .AddDirectory(Sibling, Profile, ".config");

    /// <summary>
    /// The four records NTFS holds back on every volume must not raise the "some of this could not
    /// be read" caveat.
    ///
    /// <para>Records 12 to 15 are marked in use and given neither a <c>$FILE_NAME</c> nor an
    /// <c>$ATTRIBUTE_LIST</c>, which is exactly the shape the parser calls unreadable — so without
    /// a carve-out the tree reports every drive as incompletely read, always, and the caveat
    /// carries no information at all. This is the same fact that stopped
    /// <see cref="MftVolumeIndexBuilder"/>'s fast path engaging on any real machine for six weeks;
    /// see <c>MftVolumeIndexTests.BuildsAnIndexOverTheReservedRecordsEveryNtfsVolumeCarries</c>.</para>
    ///
    /// <para>Both ends of the bound are pinned. Record 11 is <c>$Extend</c>, the last of the
    /// <em>named</em> metadata files, and a torn one of those is real damage — so the caveat must
    /// still be raised there, or the carve-out has been widened into a blanket.</para>
    /// </summary>
    [Fact]
    public void TheReservedRecordsEveryNtfsVolumeCarriesRaiseNoCaveat()
    {
        using var ordinary = Tree()
            .AddRecordWithNoIdentityAtAll(12)
            .AddRecordWithNoIdentityAtAll(13)
            .AddRecordWithNoIdentityAtAll(14)
            .AddRecordWithNoIdentityAtAll(15)
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .Build();

        var tree = MftExploreReader.Read(ordinary, Root, onProgress: null, default);

        Assert.False(
            tree.HasUnknownSizes,
            "A volume with nothing wrong with it was reported as incompletely read.");

        Assert.Equal(4096, tree.TotalBytes);

        using var damaged = Tree()
            .AddRecordWithNoIdentityAtAll(11)
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .Build();

        Assert.True(MftExploreReader.Read(damaged, Root, onProgress: null, default).HasUnknownSizes);
    }

    /// <summary>
    /// The difference from <see cref="MftVolumeIndex"/>, stated as an assertion rather than as a
    /// comment. That index keeps a name for a directory only, because naming every file on a volume
    /// costs a string per record and it never needs one; a picture of the disk is mostly files, so
    /// this reader keeps them all.
    ///
    /// <para>The contrast runs both ways on one fixture, so neither half can pass vacuously: the
    /// index finds the directory by name and does not find the file, while the tree names the file
    /// and rebuilds its whole path.</para>
    /// </summary>
    [Fact]
    public void NamesFilesAsWellAsDirectories()
    {
        var fixture = Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4000)
            .AddFile(21, Nested, "sha512.tgz", allocated: 2048, logical: 1500);

        using var source = fixture.Build();
        var tree = MftExploreReader.Read(source, Root, onProgress: null, default);

        Assert.Equal("a.tgz", tree.NameOf(20));
        Assert.Equal(@"C:\Users\testuser\.npm-cache\a.tgz", tree.PathOf(20));
        Assert.Equal(@"C:\Users\testuser\.npm-cache\content-v2\sha512.tgz", tree.PathOf(21));
        Assert.Equal(5500, tree.TotalBytes);
        Assert.Equal(1500, tree.SizeOf((int)Nested));

        using var again = fixture.Build();
        Assert.True(MftVolumeIndexBuilder.TryBuild(again, out var index));

        Assert.NotEmpty(index.FindDirectoriesNamed("content-v2"));
        Assert.Empty(index.FindDirectoriesNamed("a.tgz"));
    }

    /// <summary>
    /// Best effort, against the strictness of the index built from the same table. A record neither
    /// can read costs the index the whole volume — its numbers decide deletions, so a total that is
    /// short is worse than no total — and costs the picture one record and an honest caveat.
    ///
    /// <para>Both halves are asserted on one fixture, because the value of the contrast is that it
    /// is the <em>same</em> table. Asserting the tolerant half alone would stay green if the strict
    /// policy quietly relaxed to match it, which is the direction that matters.</para>
    ///
    /// <para>What is asserted is the tree, not <see cref="ExploreTree.HasUnknownSizes"/>. That flag
    /// cannot discriminate here: it is raised on the root by any unreadable record at all, and every
    /// table carries four — NTFS holds records 12 to 15 back for future metadata and gives them
    /// neither a <c>$FILE_NAME</c> nor an <c>$ATTRIBUTE_LIST</c>, which is the exact shape the parser
    /// calls unreadable, as
    /// <see cref="MftVolumeIndexTests.BuildsAnIndexOverTheReservedRecordsEveryNtfsVolumeCarries"/>
    /// records having measured on a real volume. Asserting it here would pass with this record
    /// removed.</para>
    /// </summary>
    [Fact]
    public void KeepsGoingPastARecordTheIndexWouldAbandonTheVolumeOver()
    {
        var fixture = Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4000)
            .AddRecordWithNoIdentityAtAll(21);

        using var strict = fixture.Build();
        Assert.False(MftVolumeIndexBuilder.TryBuild(strict, out _));

        using var source = fixture.Build();
        var tree = MftExploreReader.Read(source, Root, onProgress: null, default);

        Assert.Equal(4000, tree.TotalBytes);
        Assert.Equal("a.tgz", tree.NameOf(20));
        Assert.Equal(@"C:\Users\testuser\.npm-cache\a.tgz", tree.PathOf(20));
    }

    /// <summary>
    /// A region of the table that cannot be read ends the pass and keeps what was gathered — again
    /// where the index refuses outright rather than answer short.
    ///
    /// <para>What survived the short read is what is asserted, for the reason given in
    /// <see cref="KeepsGoingPastARecordTheIndexWouldAbandonTheVolumeOver"/>: the root's
    /// unknown-size flag is already raised by the reserved records every table carries, so it cannot
    /// show that this short read was noticed.</para>
    /// </summary>
    [Fact]
    public void KeepsWhatItGatheredWhenTheTableStopsAnsweringPartWayThrough()
    {
        var fixture = Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4000)
            .AddFile(21, Cache, "unreachable.tgz", allocated: 4_000_000_000, logical: 4_000_000_000)
            .UnreadableFrom(21);

        using var strict = fixture.Build();
        Assert.False(MftVolumeIndexBuilder.TryBuild(strict, out _));

        using var source = fixture.Build();
        var tree = MftExploreReader.Read(source, Root, onProgress: null, default);

        Assert.Equal(4000, tree.TotalBytes);
        Assert.Equal(@"C:\Users\testuser\.npm-cache\a.tgz", tree.PathOf(20));
        Assert.DoesNotContain("unreachable.tgz", Reachable(tree).Select(tree.NameOf));
    }

    /// <summary>
    /// A record whose <c>$DATA</c> lives in an extension record carries no size at all, which is not
    /// a size of zero. On a real volume this is ordinary rather than a fault — every fragmented file
    /// sampled during §5.5's measurement was in this shape — so the answer has to be a total that
    /// says it is a lower bound, and not the absence of a total.
    ///
    /// <para>The unknown has to reach every level above it, and stop at the branch that does not
    /// contain it: a sibling directory fully described is still fully described. The chain is what
    /// is asserted rather than the root's own flag, which every table raises regardless — see
    /// <see cref="KeepsGoingPastARecordTheIndexWouldAbandonTheVolumeOver"/>.</para>
    /// </summary>
    [Fact]
    public void CarriesARecordWithNoEstablishedSizeUpItsWholeParentChain()
    {
        using var source = Tree()
            .AddFile(20, Nested, "known.tgz", allocated: 4096, logical: 4000)
            .AddFileWithDataInAnExtensionRecord(21, Nested, "fragmented.tgz")
            .AddExtensionRecord(22, baseRecordNumber: 21)
            .AddFile(23, Sibling, "settings.json", allocated: 1024, logical: 1000)
            .Build();

        var tree = MftExploreReader.Read(source, Root, onProgress: null, default);

        Assert.True(tree.HasUnknownSizeBelow(21));
        Assert.True(tree.HasUnknownSizeBelow((int)Nested));
        Assert.True(tree.HasUnknownSizeBelow((int)Cache));
        Assert.True(tree.HasUnknownSizeBelow((int)Profile));

        Assert.False(tree.HasUnknownSizeBelow((int)Sibling));

        Assert.Equal(4000, tree.SizeOf((int)Nested));
        Assert.Equal(5000, tree.TotalBytes);
    }

    /// <summary>
    /// A parent outside the table is ordinary on a live volume: a directory removed mid-read, or a
    /// table that grew after its size was measured. Such a record cannot be reached from the root,
    /// so it draws nothing — and keeping it would index an array by a number that is not in it.
    /// </summary>
    [Fact]
    public void DropsARecordWhoseParentIsNotInTheTable()
    {
        using var source = Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4000)
            .AddFile(21, parent: 9000, "orphan.tgz", allocated: 8192, logical: 8000)
            .Build();

        var tree = MftExploreReader.Read(source, Root, onProgress: null, default);

        Assert.Equal(4000, tree.TotalBytes);
        Assert.DoesNotContain("orphan.tgz", Reachable(tree).Select(tree.NameOf));
    }

    /// <summary>
    /// The root is record 5 by the format, carries the volume's own path rather than the "." NTFS
    /// gives it, and is its own parent.
    ///
    /// <para>Forced rather than trusted, which is why the fixture blanks record 5: a table whose
    /// root did not parse would otherwise leave the root absent and every directory on the volume
    /// unreachable with it — a whole volume drawn as empty, from one record.</para>
    /// </summary>
    [Fact]
    public void RootsTheTreeAtRecordFiveEvenWhenThatRecordDidNotParse()
    {
        using var source = Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4000)
            .AddUnused(MftRecord.RootRecordNumber)
            .Build();

        var tree = MftExploreReader.Read(source, Root, onProgress: null, default);

        Assert.Equal((int)MftRecord.RootRecordNumber, tree.RootNode);
        Assert.Equal(Root, tree.NameOf(tree.RootNode));
        Assert.Equal(Root, tree.PathOf(tree.RootNode));
        Assert.Equal(tree.RootNode, tree.ParentOf(tree.RootNode));
        Assert.True(tree.IsDirectory(tree.RootNode));

        Assert.Equal(4000, tree.TotalBytes);
        Assert.Equal(@"C:\Users\testuser\.npm-cache\a.tgz", tree.PathOf(20));
    }

    /// <summary>
    /// A junction has no children in the table however much its path appears to hold, so a reader
    /// that did not mark it would draw a subtree with nothing in it and no explanation.
    /// </summary>
    [Fact]
    public void MarksAReparsePointAsALink()
    {
        using var source = Tree()
            .AddDirectoryLink(30, Profile, "linked-cache")
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4000)
            .Build();

        var tree = MftExploreReader.Read(source, Root, onProgress: null, default);

        Assert.True(tree.IsLink(30));
        Assert.True(tree.IsDirectory(30));
        Assert.False(tree.IsLink((int)Cache));
        Assert.False(tree.IsLink(20));
    }

    /// <summary>
    /// The table states its own record count up front, which is what lets this route drive a real
    /// progress bar where the walk can only be indeterminate. The reports have to arrive while the
    /// pass runs and rise, or the bar is decoration.
    /// </summary>
    [Fact]
    public void ReportsHowFarThroughTheTableItHasRead()
    {
        using var source = Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4000)
            .AddUnused(70_000)
            .Build();

        var reports = new List<long>();

        MftExploreReader.Read(source, Root, reports.Add, default);

        Assert.Equal([0, 65_536], reports);
    }

    private static List<int> Reachable(ExploreTree tree)
    {
        var reached = new List<int>();
        var pending = new Stack<int>();
        pending.Push(tree.RootNode);

        while (pending.TryPop(out var node))
        {
            reached.Add(node);

            foreach (var child in tree.ChildrenOf(node))
            {
                pending.Push(child);
            }
        }

        return reached;
    }
}
