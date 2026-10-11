using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a drawing of an older tree of the same place is read in the tree the page holds now: each
/// shape as its counterpart there, and each node there where the drawing draws its counterpart.
/// </summary>
public sealed class MapReadingTests
{
    /// <summary>
    /// The newer tree numbered the same file otherwise, so a shape is read as the node that is the same
    /// file, and one the newer tree does not hold is read as nothing (§7.1).
    /// </summary>
    [Fact]
    public void AShapeIsReadAsItsCounterpartOrAsNothing()
    {
        var older = Drawings.Tree(["file0", "file1"]);
        var newer = Drawings.Tree(["early", "file0"]);
        var reading = MapReading.Of(older, newer)!;

        Assert.Equal(new ExploreHit(Node(newer, "file0"), 1000), reading.Read(new ExploreHit(Node(older, "file0"), 1000)));
        Assert.Null(reading.Read(new ExploreHit(Node(older, "file1"), 1000)));
    }

    /// <summary>A block that stands for something other than a node is not a number in either tree.</summary>
    [Fact]
    public void ABlockIsReadAsItIs()
    {
        var reading = MapReading.Of(Drawings.Tree(["file0"]), Drawings.Tree(["early", "file0"]))!;
        var free = new ExploreHit(ExploreTile.FreeSpace, 5000);

        Assert.Equal(free, reading.Read(free));
    }

    /// <summary>
    /// What the page names in its tree is outlined where the older drawing draws its counterpart, and
    /// what that drawing has no counterpart of is outlined nowhere.
    /// </summary>
    [Fact]
    public void WhatThePageNamesIsFoundInTheDrawing()
    {
        var older = Drawings.Tree(["file0", "file1"]);
        var newer = Drawings.Tree(["early", "file0"]);
        var reading = MapReading.Of(older, newer)!;

        Assert.Equal(
            [Node(older, "file0")],
            reading.InDrawing(new HashSet<int> { Node(newer, "file0"), Node(newer, "early") }));
    }

    /// <summary>A drawing of the page's own tree is read as it is.</summary>
    [Fact]
    public void ADrawingOfThePagesTreeIsReadAsItIs()
    {
        var tree = Drawings.Tree();
        var reading = MapReading.Of(tree, tree)!;
        var nodes = new HashSet<int> { 1, 2 };

        Assert.Equal(new ExploreHit(1, 1000), reading.Read(new ExploreHit(1, 1000)));
        Assert.Same(nodes, reading.InDrawing(nodes));
    }

    /// <summary>A drawing of another place, or a page with no tree, cannot be read at all.</summary>
    [Fact]
    public void ADrawingOfAnotherPlaceIsNotRead()
    {
        var tree = Drawings.Tree();

        Assert.Null(MapReading.Of(tree, Drawings.Tree(root: @"D:\")));
        Assert.Null(MapReading.Of(tree, current: null));
    }

    private static int Node(ExploreTree tree, string name) =>
        tree.ChildrenOf(tree.RootNode).ToArray().Single(node => tree.NameOf(node) == name);
}
