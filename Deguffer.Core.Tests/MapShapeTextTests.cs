using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
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
            MapShapeText.Name("Documents", 450, 1000, "Users", lowerBound: false));

    [Fact]
    public void AShapeBesideTheVolumeIsAShareOfTheDrive() =>
        Assert.Equal(
            $"Free space, {FreeSpace.Format(250)}, 25% of the drive",
            MapShapeText.Name("Free space", 250, 1000, ofName: null, lowerBound: false));

    [Fact]
    public void WhatHoldsNothingHasNoShareToGive() =>
        Assert.Equal($"Empty, {FreeSpace.Format(0)}", MapShapeText.Name("Empty", 0, 0, "Users", lowerBound: false));

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
            MapShapeText.Help(new MapShapeFacts("Documents", "Folder", Opens: true, LowerBound: false), ExploreVerdict.Unclassified));

    /// <summary>A refusal is said with its reason, before Delete is pressed (§7.1).</summary>
    [Fact]
    public void AShapeThatCannotBeRemovedSaysWhy() =>
        Assert.Equal(
            "File. It cannot be removed. Windows needs it.",
            MapShapeText.Help(new MapShapeFacts("pagefile.sys", "File", Opens: false, LowerBound: false), ExploreVerdict.Refuse("Windows needs it.")));

    [Fact]
    public void APageThatRemovesNothingSaysNothingOfRemoving() =>
        Assert.Equal(
            "Program. Enter opens it.",
            MapShapeText.Help(new MapShapeFacts("explorer.exe", "Program", Opens: true, LowerBound: false), removal: null));

    /// <summary>A shape's label is its name, then its size, two spaces apart, on every page and for every block.</summary>
    [Fact]
    public void ALabelIsTheNameThenTheSize() =>
        Assert.Equal($"Documents  {FreeSpace.Format(450)}", MapShapeText.Caption("Documents", 450).Text(MapLettering.Standard, 1));

    /// <summary>
    /// §7.1: a shape the scan could not read all of says its size is a lower bound, in its name and
    /// on its label, as its row does. One it read whole does not.
    /// </summary>
    [Fact]
    public void AShapeWhoseSizeIsALowerBoundSaysSo()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode,
        [
            new ExploreChild("refused", IsDirectory: true, IsLink: false, Size: 450, SizeUnknown: true),
            new ExploreChild("plain.bin", IsDirectory: false, IsLink: false, Size: 450),
        ]);
        var tree = builder.Build(ExploreChildOrder.ByName);
        var refused = tree.ChildrenOf(tree.RootNode).ToArray().Single(node => tree.NameOf(node) == "refused");
        var plain = tree.ChildrenOf(tree.RootNode).ToArray().Single(node => tree.NameOf(node) == "plain.bin");

        Assert.Equal($"refused, {FreeSpace.Format(450)}, 45% of Users{ExploreNodeText.Unread}", MapShapeText.Name("refused", 450, 1000, "Users", lowerBound: true));
        Assert.Equal($"refused  ≥ {FreeSpace.Format(450)}", MapShapeText.Caption(tree, refused).Text(MapLettering.Standard, 1));
        Assert.Equal($"plain.bin  {FreeSpace.Format(450)}", MapShapeText.Caption(tree, plain).Text(MapLettering.Standard, 1));
    }
}
