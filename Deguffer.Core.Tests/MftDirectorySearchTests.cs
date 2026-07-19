using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Finding directories by name in the volume index — new capability, because the index was built to
/// resolve a known path top-down and exposed nothing that searches.
///
/// The names are already in the table, so this is a linear pass rather than a new structure. What
/// needs proving is the path rebuilt from each hit: a wrong path here is not a wrong number, it is
/// a wrong deletion target.
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

        Assert.True(MftVolumeIndexBuilder.TryBuild(source, out var index));
        return index;
    }

    [Fact]
    public void FindsEveryDirectoryOfThatNameWithItsFullPathRebuilt()
    {
        var found = Build(Tree()).FindDirectoriesNamed("obj");

        // Compared as a set: the table is walked in record order, which is an implementation detail
        // no caller depends on.
        Assert.Equal(
            [@"Source\Assets\obj", @"Source\Example.Tests\obj", @"Source\Example\obj"],
            found.Select(components => string.Join('\\', components)).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// The search must not become a name-prefix match. <c>bin</c> is a sibling of the same shape and
    /// is explicitly out of scope, so a rule that returned it would offer directories that can hold
    /// files no build reproduces.
    /// </summary>
    [Fact]
    public void DoesNotReturnDirectoriesWithADifferentName()
    {
        var found = Build(Tree()).FindDirectoriesNamed("obj");

        Assert.DoesNotContain(found, components => components.Contains("bin"));
    }

    /// <summary>
    /// NTFS is case-insensitive, and a project built on a machine that wrote <c>OBJ</c> holds the
    /// same output as one that wrote <c>obj</c>.
    /// </summary>
    [Fact]
    public void MatchesTheNameWithoutRegardToCase()
    {
        var index = Build(new MftFixture()
            .AddDirectory(Source, MftRecord.RootRecordNumber, "Source")
            .AddDirectory(FirstProject, Source, "Example")
            .AddDirectory(FirstObj, FirstProject, "OBJ"));

        Assert.Equal([["Source", "Example", "OBJ"]], index.FindDirectoriesNamed("obj").Select(c => c.ToArray()));
    }

    [Fact]
    public void FindsNothingWhenNoDirectoryCarriesTheName()
    {
        Assert.Empty(Build(Tree()).FindDirectoriesNamed("node_modules"));
    }

    /// <summary>
    /// Only directories carry names in the table, so a file called <c>obj</c> is invisible to this
    /// search — which is the correct answer, since only a directory can be a deletion target.
    /// </summary>
    [Fact]
    public void IgnoresFilesThatShareTheName()
    {
        var index = Build(Tree().AddFile(20, Source, "obj", allocated: 4096, logical: 4096));

        Assert.Equal(3, index.FindDirectoriesNamed("obj").Count);
    }

    /// <summary>
    /// A record whose parent chain does not reach the root cannot have a path rebuilt for it. It is
    /// dropped rather than guessed at: a partially-rebuilt path would name a real directory
    /// somewhere else entirely.
    /// </summary>
    [Fact]
    public void DropsAHitWhoseParentChainDoesNotReachTheRoot()
    {
        // Parent 40 is never added, so the chain from this record dies at an absent record.
        var index = Build(Tree().AddDirectory(30, 40, "obj"));

        var found = index.FindDirectoriesNamed("obj");

        Assert.Equal(3, found.Count);
        Assert.All(found, components => Assert.Equal("Source", components[0]));
    }

    [Fact]
    public void FindsADirectoryImmediatelyBelowTheVolumeRoot()
    {
        var index = Build(new MftFixture().AddDirectory(Source, MftRecord.RootRecordNumber, "obj"));

        Assert.Equal([["obj"]], index.FindDirectoriesNamed("obj").Select(c => c.ToArray()));
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
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', Tree()));

        var found = await scanner.TryFindDirectoriesNamedAsync("obj", @"C:\Source\Example");

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
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', Tree()));

        var found = await scanner.TryFindDirectoriesNamedAsync("obj", @"C:\Source\Example");

        Assert.DoesNotContain(@"C:\Source\Example.Tests\obj", found!);
    }

    /// <summary>
    /// Null rather than an empty list where no index can be built. The caller cannot otherwise tell
    /// "the fast route is unavailable" from "there are none", and §5.5 requires it to know which.
    /// </summary>
    [Fact]
    public async Task TheScannerReportsNoIndexAsNullSoTheCallerKnowsToWalk()
    {
        var scanner = new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

        Assert.Null(await scanner.TryFindDirectoriesNamedAsync("obj", @"C:\Source"));
    }
}
