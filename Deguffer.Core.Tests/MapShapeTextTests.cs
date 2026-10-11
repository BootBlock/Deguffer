using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a screen reader says of a shape on the map: its name, its size and its share of what holds
/// it, then what it is, whether Enter opens it, and whether Delete removes it or why it will not.
/// </summary>
public sealed class MapShapeTextTests
{
    [Fact]
    public void TheNameSaysTheSizeAndTheShareOfWhatHoldsIt() =>
        Assert.Equal(
            $"Documents, {FreeSpace.Format(450)}, 45% of Users",
            MapShapeText.Name("Documents", 450, 1000, "Users"));

    [Fact]
    public void AShapeBesideTheVolumeIsAShareOfTheDrive() =>
        Assert.Equal(
            $"Free space, {FreeSpace.Format(250)}, 25% of the drive",
            MapShapeText.Name("Free space", 250, 1000, ofName: null));

    [Fact]
    public void WhatHoldsNothingHasNoShareToGive() =>
        Assert.Equal($"Empty, {FreeSpace.Format(0)}", MapShapeText.Name("Empty", 0, 0, "Users"));

    /// <summary>A shape that is there is never "0%", and one that is the whole of its folder is "all".</summary>
    [Theory]
    [InlineData(1, 1000, "under 1%")]
    [InlineData(5, 1000, "1%")]
    [InlineData(4, 1000, "under 1%")]
    [InlineData(0, 1000, "0%")]
    [InlineData(996, 1000, "over 99%")]
    [InlineData(1000, 1000, "all")]
    [InlineData(333, 1000, "33%")]
    public void TheShareIsToTheNearestPercentAndNeverHidesAShape(long bytes, long of, string expected) =>
        Assert.Equal(expected, MapShapeText.Share(bytes, of));

    [Fact]
    public void AFolderThatOpensAndCanBeRemovedSaysBoth() =>
        Assert.Equal(
            "Folder. Enter opens it. Delete sends it to the Recycle Bin, after asking.",
            MapShapeText.Help(new MapShapeFacts("Documents", "Folder", Opens: true, ExploreVerdict.Unclassified)));

    /// <summary>A refusal is said with its reason, before Delete is pressed (§7.1).</summary>
    [Fact]
    public void AShapeThatCannotBeRemovedSaysWhy() =>
        Assert.Equal(
            "File. It cannot be removed. Windows needs it.",
            MapShapeText.Help(new MapShapeFacts("pagefile.sys", "File", Opens: false, ExploreVerdict.Refuse("Windows needs it."))));

    [Fact]
    public void APageThatRemovesNothingSaysNothingOfRemoving() =>
        Assert.Equal(
            "Program. Enter opens it.",
            MapShapeText.Help(new MapShapeFacts("explorer.exe", "Program", Opens: true, Removal: null)));

    /// <summary>Every block has a name and a sentence saying nothing here acts on it, and a node has neither.</summary>
    [Fact]
    public void EveryBlockIsNamedAndSaysItCannotBeActedOn()
    {
        foreach (var block in (int[])[ExploreTile.Aggregated, ExploreTile.FreeSpace, ExploreTile.Unaccounted, ExploreTile.ShadowCopies, ExploreTile.ReservedStorage])
        {
            Assert.NotEmpty(MapShapeText.BlockName(block));
            Assert.Matches("one by one|cannot be opened or removed", MapShapeText.BlockHelp(block));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => MapShapeText.BlockName(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapShapeText.BlockHelp(0));
    }
}
