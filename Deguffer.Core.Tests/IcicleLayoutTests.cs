using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The icicle's claim is narrower than the treemap's and easier to hold to: one row per level, and
/// width exactly proportional to size within a row. Those two together are what make it the layout
/// that stays still while a scan is still filling in.
/// </summary>
public sealed class IcicleLayoutTests
{
    private const float Width = 800;
    private const float Height = 400;
    private const float RowHeight = 20;

    [Fact]
    public void EachLevelSitsOnItsOwnRow()
    {
        var tiles = Layout(NestedTree(depth: 5), LayoutLimits.Default);

        foreach (var tile in tiles)
        {
            Assert.Equal(tile.Depth * RowHeight, tile.Y);
            Assert.Equal(RowHeight, tile.Height);
        }
    }

    [Fact]
    public void WidthIsProportionalToSizeWithinARow()
    {
        var tree = TreeOf(500, 300, 200);
        var tiles = Layout(tree, LayoutLimits.Default);

        foreach (var tile in tiles.Where(t => t.Depth == 1))
        {
            Assert.InRange(tile.Width, Width * tile.Bytes / 1000.0 - 1, Width * tile.Bytes / 1000.0 + 1);
        }
    }

    /// <summary>
    /// Boundaries come from a running total rather than from each child's own rounded width, so the
    /// last child of a wide row lands exactly on the edge instead of drifting off it.
    /// </summary>
    [Fact]
    public void TheChildrenTileTheirParentWithNoGapAndNoOverlap()
    {
        var tiles = Layout(TreeOf([.. Enumerable.Range(1, 40).Select(i => (long)i)]), LayoutLimits.Default)
            .Where(t => t.Depth == 1)
            .OrderBy(t => t.X)
            .ToList();

        Assert.Equal(0, tiles[0].X);

        for (var i = 1; i < tiles.Count; i++)
        {
            Assert.InRange(tiles[i].X, tiles[i - 1].X + tiles[i - 1].Width - 0.01, tiles[i - 1].X + tiles[i - 1].Width + 0.01);
        }

        var last = tiles[^1];
        Assert.InRange(last.X + last.Width, Width - 0.5, Width + 0.01);
    }

    [Fact]
    public void WhatIsTooNarrowToDrawIsAggregatedRatherThanDropped()
    {
        var limits = LayoutLimits.Default with { MinimumTileSize = 20, MaximumDepth = 2 };
        var tree = TreeOf([1_000_000, .. Enumerable.Repeat(1L, 500)]);

        var tiles = Layout(tree, limits);
        var aggregate = Assert.Single(tiles, t => t.IsAggregate);

        Assert.Equal(500, aggregate.Bytes);
    }

    /// <summary>
    /// Rows stop at the bottom of the canvas rather than being squeezed thinner to fit. An icicle
    /// over a deep tree is meant to be descended into, and inventing thinner rows would trade the
    /// one thing this layout is good at for the one thing it is not.
    /// </summary>
    [Fact]
    public void RowsStopAtTheBottomOfTheCanvas()
    {
        var tiles = Layout(NestedTree(depth: 60), LayoutLimits.Default with { MinimumTileSize = 0.01f, MaximumDepth = 60 });

        Assert.All(tiles, t => Assert.InRange(t.Y + t.Height, 0, Height));
    }

    [Fact]
    public void ADeepTreeDoesNotOverflowTheStack()
    {
        var tree = NestedTree(depth: 5000);

        Assert.NotEmpty(Layout(tree, LayoutLimits.Default with { MinimumTileSize = 0.0001f, MaximumDepth = 5000 }));
    }

    [Fact]
    public void ACanvasShorterThanOneRowDrawsNothing()
    {
        var tree = TreeOf(100);

        Assert.Empty(IcicleLayout.Compute(tree, tree.RootNode, Width, RowHeight - 1, Rows(LayoutLimits.Default)));
    }

    /// <summary>
    /// Under a name order the children too narrow to draw are scattered through the row rather than
    /// gathered at its end, and every child after the first of them is still a child that has to be
    /// drawn. Stopping at the first narrow one — which is all a size order ever needs — loses them.
    /// </summary>
    [Fact]
    public void DrawsEveryChildWideEnoughEvenWhereTheNarrowOnesComeFirst()
    {
        var tiles = Layout(ScatteredTree(), LayoutLimits.Default);

        var drawn = tiles
            .Where(tile => tile.Depth == 1 && !tile.IsAggregate)
            .Select(tile => tile.Bytes)
            .OrderByDescending(bytes => bytes)
            .ToList();

        Assert.Equal([400L, 300L, 298L], drawn);
    }

    /// <summary>
    /// The scattered narrow children still add up to something, and it is still stated once: one
    /// rectangle, carrying their bytes, in the space at the end of the row that the drawn children
    /// did not take. A gap there would read as free space rather than as detail withheld.
    /// </summary>
    [Fact]
    public void GathersTheNarrowChildrenIntoOneRectangleWhereverTheySatInTheOrder()
    {
        var tiles = Layout(ScatteredTree(), LayoutLimits.Default).Where(t => t.Depth == 1).ToList();

        var aggregate = Assert.Single(tiles, tile => tile.IsAggregate);

        Assert.Equal(2, aggregate.Bytes);
        Assert.Equal(1000, tiles.Sum(tile => tile.Bytes));
        Assert.InRange(aggregate.X + aggregate.Width, Width - 0.01, Width + 0.01);
    }

    private static IReadOnlyList<ExploreTile> Layout(ExploreTree tree, LayoutLimits limits) =>
        IcicleLayout.Compute(tree, tree.RootNode, Width, Height, Rows(limits));

    /// <summary>
    /// The row height is a limit like the others, so the tests state it the same way rather than
    /// carrying a second number that has to be kept in step with the one the layout reads.
    /// </summary>
    private static LayoutLimits Rows(LayoutLimits limits) => limits with { RowHeight = RowHeight };

    private static ExploreTree TreeOf(params long[] sizes)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. sizes.Select((size, i) => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, size))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// A snapshot of a scan in progress: ordered by name, so the two one-byte children sit between
    /// the three that are wide enough to draw. At 800 pixels across 1000 bytes the floor of three
    /// pixels is four bytes, so exactly those two are too narrow.
    /// </summary>
    private static ExploreTree ScatteredTree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("a.bin", IsDirectory: false, IsLink: false, Size: 400),
            new ExploreChild("b.bin", IsDirectory: false, IsLink: false, Size: 1),
            new ExploreChild("c.bin", IsDirectory: false, IsLink: false, Size: 300),
            new ExploreChild("d.bin", IsDirectory: false, IsLink: false, Size: 1),
            new ExploreChild("e.bin", IsDirectory: false, IsLink: false, Size: 298),
        ]);

        return builder.Build(ExploreChildOrder.ByName);
    }

    private static ExploreTree NestedTree(int depth)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var parent = ExploreTreeBuilder.RootNode;

        for (var i = 0; i < depth; i++)
        {
            parent = builder.AddChildren(parent, [
                new ExploreChild($"dir{i}", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000),
            ]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
