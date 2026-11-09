using Deguffer.Core.Duplicates;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>What <see cref="FolderIndex{T}"/> finds, and the lookups built on it keeping their answers (#706).</summary>
public sealed class FolderIndexTests
{
    private static IReadOnlyList<string[]> Walk(FolderIndex<string> index, string place)
    {
        List<string[]> found = [];

        foreach (var filed in index.Holding(place))
        {
            found.Add(filed);
        }

        return found;
    }

    [Fact]
    public void TheWalkComesToTheDeepestFolderFirstAndKeepsTheFilingOrderAtEach()
    {
        var index = new FolderIndex<string>(["outer", "first", "second"], item => item switch
        {
            "outer" => [@"D:\a"],
            _ => [@"D:\a\b"],
        });

        Assert.Equal([["first", "second"], ["outer"]], Walk(index, @"D:\a\b\c.bin"));
    }

    [Fact]
    public void AVolumeRootIsFoundFromAnythingOnIt()
    {
        var index = new FolderIndex<string>(["root"], _ => [@"D:\"]);

        Assert.Equal([["root"]], Walk(index, @"D:\a\b"));
        Assert.Equal([["root"]], Walk(index, @"D:\"));
    }

    [Fact]
    public void AFolderOnlySharingAPrefixOfTheNameIsNotFound()
    {
        var index = new FolderIndex<string>(["sibling"], _ => [@"D:\ab"]);

        Assert.Empty(Walk(index, @"D:\abc\x"));
    }

    [Fact]
    public void ComparesIgnoringCaseAsLongPathContainsDoes()
    {
        var index = new FolderIndex<string>(["tool"], _ => [@"D:\Tool"]);

        Assert.Equal([["tool"]], Walk(index, @"d:\TOOL\x"));
    }

    /// <summary><see cref="LongPath.Contains(string, string)"/> takes only a backslash as the end of a folder, so the walk must too.</summary>
    [Fact]
    public void TheWalkDoesNotTakeAForwardSlashAsTheEndOfAFolder()
    {
        var index = new FolderIndex<string>(["tool"], _ => [@"D:\a"]);

        Assert.Empty(Walk(index, "D:\\a/b"));
    }

    [Fact]
    public void AnItemNamedTwiceUnderOneFolderIsFiledOnce()
    {
        var index = new FolderIndex<string>(["tool"], _ => [@"D:\Tool", @"d:\tool\"]);

        Assert.Equal([["tool"]], Walk(index, @"D:\Tool\x"));
    }

    [Fact]
    public void PlacesAndAboveFilesAFolderUnderEveryFolderHoldingIt()
    {
        var folder = ReachedFolder.At(@"D:\a\b", new FakeVolumeInventory());
        var index = new FolderIndex<string>(["located"], _ => folder.PlacesAndAbove());

        Assert.Equal(["located"], index.At(@"D:\a").ToArray());
        Assert.Equal(["located"], index.At(@"D:\").ToArray());
        Assert.Equal(["located"], index.At(@"D:\a\b").ToArray());
        Assert.Empty(index.At(@"D:\a\c").ToArray());
    }

    [Theory]
    [InlineData(@"D:\a", @"D:\a", 0)]
    [InlineData(@"D:\a", @"D:\A\b", 1)]
    [InlineData(@"D:\a", @"D:\a\b\c", 2)]
    [InlineData(@"D:\", @"D:\a\b\c", 3)]
    [InlineData(@"\\server\share\x", @"\\server\share\x\y", 1)]
    public void LevelsAreTheNamesBelowTheFolder(string outer, string place, int levels)
    {
        var folder = ReachedFolder.At(outer, new FakeVolumeInventory());
        var relative = folder.PathTo(place)!;

        Assert.Equal(levels, folder.LevelsTo(place));
        Assert.Equal(levels, relative == "." ? 0 : relative.Split('\\').Length);
    }

    /// <summary>A place keeps the spelling it was given, which can match as text and resolve to somewhere else.</summary>
    [Fact]
    public void AnUnresolvedSpellingIsCountedWhereItLeads()
    {
        var volumes = new FakeVolumeInventory();
        var users = ReachedFolder.At(@"D:\Users", volumes);

        Assert.Equal(2, users.LevelsTo(ReachedFolder.At("D:\\Users\\../Apps", volumes)));
    }

    [Fact]
    public void TheNearestOfAFoldersPlacesAnswers()
    {
        var volumes = new FakeVolumeInventory();
        var folder = ReachedFolder.At(@"D:\a", volumes).Joined(ReachedFolder.At(@"D:\a\b", volumes));

        Assert.Equal(1, folder.LevelsTo(@"D:\a\b\c"));
        Assert.Equal("c", folder.PathTo(@"D:\a\b\c"));
    }

    [Fact]
    public void AFolderThatDoesNotHoldThePlaceAnswersNothing()
    {
        var folder = ReachedFolder.At(@"D:\a", new FakeVolumeInventory());

        Assert.Null(folder.LevelsTo(@"D:\ab"));
        Assert.Null(folder.PathTo(@"D:\ab"));
    }

    /// <summary>The first listed names a copy in a nested program folder, as it did when every program was asked in turn.</summary>
    [Fact]
    public void TheFirstProgramListedAnswersEvenWhereALaterOneIsDeeper()
    {
        var volumes = new FakeVolumeInventory();
        ProgramFolder Program(string name, string path) =>
            new(path, ReachedFolder.At(path, volumes), name, Final: null);

        var index = new ProgramFolderIndex([Program("Outer", @"D:\Apps"), Program("Inner", @"D:\Apps\Tool")]);
        var reversed = new ProgramFolderIndex([Program("Inner", @"D:\Apps\Tool"), Program("Outer", @"D:\Apps")]);

        Assert.Equal("Outer", index.FirstHolding(@"D:\Apps\Tool\lib.dll")?.Program);
        Assert.Equal("Inner", reversed.FirstHolding(@"D:\Apps\Tool\lib.dll")?.Program);
        Assert.Null(index.FirstHolding(@"D:\Other\lib.dll"));
    }

    [Fact]
    public void AProgramIsFoundThroughTheFolderItsEntryLeadsTo()
    {
        var volumes = new FakeVolumeInventory();
        var program = new ProgramFolder(
            @"D:\Apps\Tool", ReachedFolder.At(@"D:\Apps\Tool", volumes), "Tool", ReachedFolder.At(@"E:\Apps\Tool", volumes));

        Assert.Same(program, new ProgramFolderIndex([program]).FirstHolding(@"E:\Apps\Tool\lib.dll"));
    }

    /// <summary>The first place in the given order says what holds a copy, wherever the walk comes to it.</summary>
    [Fact]
    public void TheFirstPlaceGivenAnswersEvenWhereALaterOneIsDeeper()
    {
        var places = ResolvedPlaces.Resolve(
            [(CleanedPlace.Whole(@"D:\Caches"), "outer"), (CleanedPlace.Whole(@"D:\Caches\npm"), "inner")],
            UnopenedDrive(),
            FileInformation.Default);

        var reversed = ResolvedPlaces.Resolve(
            [(CleanedPlace.Whole(@"D:\Caches\npm"), "inner"), (CleanedPlace.Whole(@"D:\Caches"), "outer")],
            UnopenedDrive(),
            FileInformation.Default);

        Assert.Equal("outer", places.WhatHolds(@"D:\Caches\npm\x.tgz"));
        Assert.Equal("inner", reversed.WhatHolds(@"D:\Caches\npm\x.tgz"));
        Assert.Null(places.WhatHolds(@"D:\Other\x.tgz"));
    }

    /// <summary>A network drive, which is never opened, so nothing here asks the disk of the machine running the suite.</summary>
    private static FakeVolumeInventory UnopenedDrive() => new FakeVolumeInventory().With(@"D:\", DriveType.Network);

    [Theory]
    [InlineData(@"D:\src\app\obj\x.dll", true)]
    [InlineData(@"D:\src\app\OBJ\deep\x.dll", true)]
    [InlineData(@"D:\src\obj", true)]
    [InlineData(@"D:\src\app\objects\x.dll", false)]
    [InlineData(@"D:\src", false)]
    [InlineData(@"D:\other\obj\x.dll", false)]
    public void APlaceFoundByNameHoldsWhatIsInAFolderWithTheName(string path, bool holds)
    {
        var place = CleanedPlace.FoldersNamed(@"D:\src", ["obj"]);

        Assert.Equal(holds, place.HoldsComparable(@"D:\src", path));
    }
}
