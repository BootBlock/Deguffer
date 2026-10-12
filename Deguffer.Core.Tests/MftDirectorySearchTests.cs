using System.Collections.Frozen;
using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Finding directories by name in the volume index, below one root, in one pass over that root's
/// subtree.
///
/// The names are already in the table, so this is a descent through the child links rather than a
/// new structure. What needs proving is where the descent stops and the path built for each hit: a
/// wrong path here is not a wrong number, it is a wrong deletion target.
/// </summary>
public class MftDirectorySearchTests
{
    // A synthetic source tree. Paths are invented rather than copied from a real machine.
    private const uint Source = 6;
    private const uint FirstProject = 7;
    private const uint FirstObj = 8;
    private const uint SecondProject = 9;
    private const uint SecondObj = 10;
    private const uint Bin = 11;
    private const uint Assets = 12;
    private const uint AssetObj = 13;

    private static readonly string[] Volume = [];
    private static readonly string[] Example = ["Source", "Example"];

    private static MftFixture Tree() => new MftFixture()
        .AddDirectory(Source, MftRecord.RootRecordNumber, "Source")
        .AddDirectory(FirstProject, Source, "Example")
        .AddDirectory(FirstObj, FirstProject, "obj")
        .AddDirectory(SecondProject, Source, "Example.Tests")
        .AddDirectory(SecondObj, SecondProject, "obj")
        .AddDirectory(Bin, FirstProject, "bin")
        .AddDirectory(Assets, Source, "Assets")
        .AddDirectory(AssetObj, Assets, "obj");

    private static MftVolumeIndex Build(MftFixture fixture)
    {
        using var source = fixture.Build();

        Assert.True(MftVolumeIndexBuilder.TryBuild(source, TableTuning.Default, out var index));
        return index;
    }

    /// <summary>Each hit as components below the root joined, in ordinal order.</summary>
    private static IEnumerable<string> Joined(IReadOnlyList<IReadOnlyList<string>>? found)
    {
        Assert.NotNull(found);
        return found.Select(components => string.Join('\\', components)).Order(StringComparer.Ordinal);
    }

    [Fact]
    public void FindsEveryDirectoryOfThatNameWithItsPathBelowTheRoot()
    {
        var found = Build(Tree()).TryFindDirectories(Volume, DirectorySearches.For("obj"));

        // Compared in order: the table is descended in record order, which is an implementation
        // detail no caller depends on.
        Assert.Equal(
            [@"Source\Assets\obj", @"Source\Example.Tests\obj", @"Source\Example\obj"],
            Joined(found));
    }

    /// <summary>
    /// The point of the query: every name in one pass, rather than one pass per name over the whole
    /// table.
    /// </summary>
    [Fact]
    public void FindsEveryNameOfTheSearchInOnePass()
    {
        var found = Build(Tree()).TryFindDirectories(Example, DirectorySearches.For("obj", "bin"));

        Assert.Equal(["bin", "obj"], Joined(found));
    }

    /// <summary>
    /// The answer is the root's own subtree and nothing else. <c>Example.Tests</c> shares the root's
    /// name as a prefix, and <c>Assets</c> holds an <c>obj</c> of its own, and neither is inside.
    /// </summary>
    [Fact]
    public void FindsNothingOutsideTheRoot()
    {
        var found = Build(Tree()).TryFindDirectories(Example, DirectorySearches.For("obj"));

        Assert.Equal(["obj"], Joined(found));
    }

    /// <summary>
    /// The search must not become a name-prefix match. <c>bin</c> is a sibling of the same shape and
    /// is not sought here, so a rule that returned it would offer directories that can hold files no
    /// build reproduces.
    /// </summary>
    [Fact]
    public void DoesNotReturnDirectoriesWithADifferentName()
    {
        var found = Build(Tree()).TryFindDirectories(Volume, DirectorySearches.For("obj"));

        Assert.DoesNotContain(found!, components => components.Contains("bin"));
    }

    /// <summary>
    /// NTFS is case-insensitive, and a project built on a machine that wrote <c>OBJ</c> holds the
    /// same output as one that wrote <c>obj</c>. The path keeps the case the table holds.
    /// </summary>
    [Fact]
    public void MatchesTheNameWithoutRegardToCase()
    {
        var index = Build(new MftFixture()
            .AddDirectory(Source, MftRecord.RootRecordNumber, "Source")
            .AddDirectory(FirstProject, Source, "Example")
            .AddDirectory(FirstObj, FirstProject, "OBJ"));

        Assert.Equal([@"Source\Example\OBJ"], Joined(index.TryFindDirectories(Volume, DirectorySearches.For("obj"))));
    }

    /// <summary>
    /// Everything below a found directory belongs to it, so the descent stops there. An <c>obj</c>
    /// inside an <c>obj</c> is part of the output already offered, and offering it as well would make
    /// two steps where one deletes the other's parent.
    /// </summary>
    [Fact]
    public void StopsAtADirectoryItFinds()
    {
        var index = Build(Tree()
            .AddDirectory(30, FirstObj, "Debug")
            .AddDirectory(31, 30, "obj")
            .AddDirectory(32, FirstObj, "bin"));

        Assert.Equal(["bin", "obj"], Joined(index.TryFindDirectories(Example, DirectorySearches.For("obj", "bin"))));
    }

    /// <summary>
    /// The source discovery's search never enters <c>.git</c> or <c>node_modules</c>, and the index
    /// stops there exactly as the walk does. A name being sought overrides that: a search for
    /// <c>node_modules</c> finds it and goes no further.
    /// </summary>
    [Fact]
    public void NeverEntersWhatTheSearchRefusesUnlessItIsSought()
    {
        var index = Build(Tree()
            .AddDirectory(30, FirstProject, ".git")
            .AddDirectory(31, 30, "obj")
            .AddDirectory(32, FirstProject, "node_modules")
            .AddDirectory(33, 32, "left-pad")
            .AddDirectory(34, 33, "obj"));

        var obj = SourceTreeBoundary.Searching(FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "obj"));
        var both = SourceTreeBoundary.Searching(FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "obj", "node_modules"));

        Assert.Equal(["obj"], Joined(index.TryFindDirectories(Example, obj)));
        Assert.Equal(["node_modules", "obj"], Joined(index.TryFindDirectories(Example, both)));
    }

    /// <summary>
    /// The walk never enters a reparse point, so a directory only reachable through one is not
    /// something an unelevated run can offer. The index sees the link as an ordinary directory in
    /// the tree, so it has to be told.
    /// </summary>
    [Fact]
    public void DoesNotReturnADirectoryReachedThroughALink()
    {
        var index = Build(Tree()
            .AddDirectoryLink(30, Source, "Linked")
            .AddDirectory(31, 30, "obj"));

        var found = Joined(index.TryFindDirectories(Volume, DirectorySearches.For("obj"))).ToList();

        Assert.DoesNotContain(@"Source\Linked\obj", found);
        Assert.Contains(@"Source\Example\obj", found);
    }

    /// <summary>
    /// And the link itself is not a candidate. It is a name pointing somewhere else; deleting it
    /// removes the pointer, which is not what a user asked for when they asked to reclaim build
    /// output. The walk does not list a link as a directory either.
    /// </summary>
    [Fact]
    public void DoesNotReturnALinkThatCarriesTheName()
    {
        // Beside a real one of the same name, so a search that returned both would still look
        // right by count.
        var index = Build(Tree().AddDirectoryLink(30, Assets, "obj"));

        var found = Joined(index.TryFindDirectories(Volume, DirectorySearches.For("obj"))).ToList();

        Assert.Contains(@"Source\Assets\obj", found);
        Assert.Equal(3, found.Count);
    }

    /// <summary>An empty list rather than null: the index answered, and there are none.</summary>
    [Fact]
    public void FindsNothingWhenNoDirectoryCarriesTheName()
    {
        var found = Build(Tree()).TryFindDirectories(Volume, DirectorySearches.For("node_modules"));

        Assert.NotNull(found);
        Assert.Empty(found);
    }

    /// <summary>
    /// Only directories carry names in the table, so a file called <c>obj</c> is invisible to this
    /// search — which is the correct answer, since only a directory can be a deletion target.
    /// </summary>
    [Fact]
    public void IgnoresFilesThatShareTheName()
    {
        var index = Build(Tree().AddFile(20, Source, "obj", allocated: 4096, logical: 4096));

        Assert.Equal(3, index.TryFindDirectories(Volume, DirectorySearches.For("obj"))!.Count);
    }

    /// <summary>
    /// A record whose parent chain does not reach the root is in no root's subtree, so no search
    /// reaches it. A path guessed for it would name a real directory somewhere else entirely.
    /// </summary>
    [Fact]
    public void DropsAHitWhoseParentChainDoesNotReachTheRoot()
    {
        // Parent 40 is never added, so the chain from this record dies at an absent record.
        var index = Build(Tree().AddDirectory(30, 40, "obj"));

        var found = index.TryFindDirectories(Volume, DirectorySearches.For("obj"));

        Assert.Equal(3, found!.Count);
        Assert.All(found, components => Assert.Equal("Source", components[0]));
    }

    [Fact]
    public void FindsADirectoryImmediatelyBelowTheVolumeRoot()
    {
        var index = Build(new MftFixture().AddDirectory(Source, MftRecord.RootRecordNumber, "obj"));

        Assert.Equal(["obj"], Joined(index.TryFindDirectories(Volume, DirectorySearches.For("obj"))));
    }

    /// <summary>
    /// The root is where the search starts, so it is never one of the answers, whatever it is
    /// called. The user approved that folder as a place to search inside, which is not the same as
    /// offering it up for deletion.
    /// </summary>
    [Fact]
    public void NeverOffersTheRootItself()
    {
        var found = Build(Tree()).TryFindDirectories(["Source", "Example", "obj"], DirectorySearches.For("obj"));

        Assert.NotNull(found);
        Assert.Empty(found);
    }

    /// <summary>
    /// Null, never an empty list, for a root the index has no subtree for. Null sends the caller to
    /// walk the root, and an empty list would tell it there is nothing there.
    /// </summary>
    [Fact]
    public void HasNoAnswerForARootItDoesNotHold()
    {
        Assert.Null(Build(Tree()).TryFindDirectories(["Source", "Elsewhere"], DirectorySearches.For("obj")));
    }

    /// <summary>
    /// A root that is a link, or is reached through one, has no subtree here: whatever it points at
    /// keeps its own place in the table. The walk follows it and is right.
    /// </summary>
    [Fact]
    public void HasNoAnswerForARootReachedThroughALink()
    {
        var index = Build(Tree()
            .AddDirectoryLink(30, Source, "Linked")
            .AddDirectory(31, 30, "Inner"));

        Assert.Null(index.TryFindDirectories(["Source", "Linked"], DirectorySearches.For("obj")));
        Assert.Null(index.TryFindDirectories(["Source", "Linked", "Inner"], DirectorySearches.For("obj")));
    }

    /// <summary>
    /// The narrowing to an approved root, asserted against the real scanner rather than a fake.
    ///
    /// This is the consent model rather than an optimisation: the index knows every directory on the
    /// volume, and it must never turn a cheap answer into permission to act on a folder the user did
    /// not approve. The provider tests drive a fake scanner, so without this the narrowing in the
    /// real one would be unexercised — and dropping it would break no test at all.
    /// </summary>
    [Fact]
    public async Task TheScannerReturnsOnlyDirectoriesInsideTheRootItWasAskedAbout()
    {
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', Tree()), tuning: RouteTuners.Table);

        var found = await scanner.TryFindDirectoriesAsync(DirectorySearches.For("obj"), @"C:\Source\Example");

        Assert.NotNull(found);
        Assert.Equal([@"C:\Source\Example\obj"], found);
    }

    /// <summary>
    /// A root name that is a prefix of a sibling's must not drag the sibling in with it:
    /// <c>Example</c> does not contain <c>Example.Tests</c>, however the strings compare.
    /// </summary>
    [Fact]
    public async Task DoesNotTreatASiblingWithASharedNamePrefixAsBeingInsideTheRoot()
    {
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', Tree()), tuning: RouteTuners.Table);

        var found = await scanner.TryFindDirectoriesAsync(DirectorySearches.For("obj"), @"C:\Source\Example");

        Assert.DoesNotContain(@"C:\Source\Example.Tests\obj", found!);
    }

    /// <summary>
    /// The root is spelled as the caller gave it, and what lies below it as the table holds it. That
    /// is how the walk spells what it finds, so the two routes hand a plan the same path.
    /// </summary>
    [Fact]
    public async Task TheScannerSpellsAPathAsTheWalkDoes()
    {
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', Tree()), tuning: RouteTuners.Table);

        var found = await scanner.TryFindDirectoriesAsync(DirectorySearches.For("OBJ", "bin"), @"C:\SOURCE\example\");

        Assert.Equal([@"C:\SOURCE\example\bin", @"C:\SOURCE\example\obj"], found);
    }

    /// <summary>
    /// Null rather than an empty list where no index can be built. The caller cannot otherwise tell
    /// "the fast route is unavailable" from "there are none", and §5.5 requires it to know which.
    /// </summary>
    [Fact]
    public async Task TheScannerReportsNoIndexAsNullSoTheCallerKnowsToWalk()
    {
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

        Assert.Null(await scanner.TryFindDirectoriesAsync(DirectorySearches.For("obj"), @"C:\Source"));
    }

    /// <summary>
    /// An approved root that is a junction, which is an ordinary way to keep source on a second
    /// drive. The table holds nothing below the link, so an answer from it would be an empty list
    /// and the plan would say the folder holds no build output. Null sends the caller to walk it.
    /// </summary>
    [Fact]
    public async Task TheScannerSendsARootReachedThroughALinkToTheWalk()
    {
        var scanner = new DirectoryScanner(
            FakeMftSourceFactory.Serving('C', Tree().AddDirectoryLink(30, Source, "Linked")),
            tuning: RouteTuners.Table);

        Assert.Null(await scanner.TryFindDirectoriesAsync(DirectorySearches.For("obj"), @"C:\Source\Linked"));
    }
}
