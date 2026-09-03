using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The treemap's whole claim is that area is proportional to size. Everything else about it —
/// squareness, shading, nesting — is in service of that being readable, so these tests assert the
/// claim first and the squareness second.
/// </summary>
public sealed class TreemapLayoutTests
{
    private const float Width = 800;
    private const float Height = 600;

    [Fact]
    public void EachChildGetsAreaInProportionToItsSize()
    {
        var tree = TreeOf(400, 300, 200, 100);
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default);

        // The root keeps a one-pixel frame around its children, so they share what is inside it
        // rather than the whole canvas.
        var available = (Width - 2) * (Height - 2);

        foreach (var tile in tiles.Where(t => t.Depth == 1))
        {
            var expected = available * tile.Bytes / 1000.0;

            Assert.InRange(tile.Width * tile.Height, expected * 0.97, expected * 1.03);
        }
    }

    [Fact]
    public void TheChildrenFillTheirParentBetweenThem()
    {
        var tree = TreeOf(400, 300, 200, 100);
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default);

        var available = (double)(Width - 2) * (Height - 2);
        var covered = tiles.Where(t => t.Depth == 1).Sum(t => (double)t.Width * t.Height);

        // The upper bound carries a tolerance rather than sitting exactly on the available area:
        // the row thicknesses are single-precision, so a set of children that fills its parent
        // exactly totals a few ten-thousandths over it.
        Assert.InRange(covered, available * 0.98, available * 1.0001);
    }

    /// <summary>
    /// The reason to squarify at all. Bruls, Huizing and van Wijk measured the slice-and-dice
    /// original reaching 304:1 across 100 items, at which point a sliver and a block of equal area
    /// do not look equal. A single alternating split would put every one of these hundred children
    /// in one strip of the canvas.
    /// </summary>
    [Fact]
    public void RectanglesStayRoughlySquareEvenWithAHundredChildren()
    {
        var tree = TreeOf([.. Enumerable.Range(1, 100).Select(i => (long)(101 - i))]);
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default);

        var worst = tiles
            .Where(t => t.Depth == 1 && t.Width > 0 && t.Height > 0)
            .Max(t => Math.Max(t.Width / t.Height, t.Height / t.Width));

        Assert.True(worst < 8, $"worst aspect ratio was {worst:F1}");
    }

    /// <summary>
    /// A view drawing the list in order relies on this: a child painted before its parent would be
    /// covered by it and vanish.
    /// </summary>
    [Fact]
    public void AParentIsAlwaysPaintedBeforeItsChildren()
    {
        var tree = NestedTree(depth: 4);
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default);

        var seen = new HashSet<int>();

        foreach (var tile in tiles.Where(t => !t.IsAggregate))
        {
            if (tile.Node != tree.RootNode)
            {
                Assert.Contains(tree.ParentOf(tile.Node), seen);
            }

            seen.Add(tile.Node);
        }
    }

    [Fact]
    public void NothingIsDrawnOutsideTheCanvas()
    {
        var tree = NestedTree(depth: 5);
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default);

        foreach (var tile in tiles)
        {
            Assert.InRange(tile.X, 0, Width);
            Assert.InRange(tile.Y, 0, Height);
            Assert.InRange(tile.X + tile.Width, 0, Width + 0.01);
            Assert.InRange(tile.Y + tile.Height, 0, Height + 0.01);
        }
    }

    [Fact]
    public void NoRectangleIsSmallerThanTheFloor()
    {
        var limits = LayoutLimits.Default with { MinimumTileSize = 6, MaximumDepth = 6 };
        var tree = TreeOf([.. Enumerable.Range(1, 2000).Select(i => (long)i)]);

        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits);

        foreach (var tile in tiles.Where(t => t.Depth > 0))
        {
            Assert.True(
                tile.Width >= limits.MinimumTileSize && tile.Height >= limits.MinimumTileSize,
                $"a {tile.Width}x{tile.Height} rectangle was drawn below the {limits.MinimumTileSize} floor");
        }
    }

    /// <summary>
    /// The omission has to be visible as a quantity. A blank corner of a treemap reads as free
    /// space rather than as detail withheld, which is the opposite of what happened.
    /// </summary>
    [Fact]
    public void WhatIsTooSmallToDrawIsAggregatedRatherThanDropped()
    {
        var limits = LayoutLimits.Default with { MinimumTileSize = 20, MaximumDepth = 2 };
        var tree = TreeOf([1_000_000, .. Enumerable.Repeat(1L, 500)]);

        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits);
        var aggregates = tiles.Where(t => t.IsAggregate).ToList();

        Assert.NotEmpty(aggregates);
        Assert.All(aggregates, a => Assert.True(a.Bytes > 0, "an aggregate stood for nothing"));
    }

    /// <summary>
    /// Every byte is either drawn or aggregated. Nothing leaves the picture silently.
    ///
    /// <para>This is the half the test above cannot see. It exercises the branch where the row is
    /// accepted — its thickness clears the floor — and a member <em>within</em> it comes out too
    /// narrow to draw. That child used to be skipped where it stood: no rectangle, no aggregate, and
    /// `index` advanced past it regardless, so its bytes were neither shown nor counted as hidden.
    /// The space then showed the parent's colour and read as free.</para>
    ///
    /// <para>The shape is the reviewer's: a rectangle whose short side is only a little over the
    /// floor, and two siblings at a 4:1 ratio, which squarification is perfectly happy to put in one
    /// row.</para>
    /// </summary>
    [Fact]
    public void EveryByteIsEitherDrawnOrCountedInAnAggregate()
    {
        var limits = LayoutLimits.Default with { MinimumTileSize = 3, MaximumDepth = 2 };

        foreach (var (width, height, sizes) in new[]
        {
            (17f, 40f, new long[] { 400, 100 }),
            (98f, 98f, new long[] { 300, 1 }),
            (400f, 300f, new long[] { 4000, 10 }),
            (1200f, 800f, new long[] { 4000, 10 }),
            (800f, 600f, new long[] { 5000, 900, 80, 7, 1 }),

            // Long and narrow, so the leftover strip runs out along its short side before the
            // children do. That is the other way a row stops being drawable: not the row that is
            // too small, but the space left to lay it into.
            (20f, 400f, new long[] { 900, 400, 120, 60, 30, 12, 5, 3, 2, 1 }),
            (400f, 20f, new long[] { 900, 400, 120, 60, 30, 12, 5, 3, 2, 1 }),
            (14f, 900f, new long[] { 5000, 2000, 400, 90, 20, 6, 2, 1 }),
        })
        {
            var tree = TreeOf(sizes);
            var tiles = TreemapLayout.Compute(tree, tree.RootNode, width, height, limits);

            var accounted = tiles
                .Where(t => t.Depth > 0)
                .Sum(t => t.Bytes);

            Assert.Equal(sizes.Sum(), accounted);
        }
    }

    [Fact]
    public void DescentStopsAtTheDepthLimit()
    {
        var tree = NestedTree(depth: 12);
        var limits = LayoutLimits.Default with { MinimumTileSize = 1, MaximumDepth = 3 };

        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits);

        Assert.Equal(3, tiles.Max(t => t.Depth));
    }

    /// <summary>
    /// The layout is iterative for this. A recursive one over a real node_modules tree is a stack
    /// overflow inside a repaint handler, which is not an exception anything can catch usefully.
    /// </summary>
    [Fact]
    public void ADeepTreeDoesNotOverflowTheStack()
    {
        var tree = NestedTree(depth: 5000);
        var limits = LayoutLimits.Default with { MinimumTileSize = 0.0001f, MaximumDepth = 5000 };

        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits);

        Assert.NotEmpty(tiles);
    }

    [Fact]
    public void AnEmptyTreeDrawsNothingRatherThanFillingTheCanvas()
    {
        var tree = TreeOf();

        Assert.Empty(TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default));
    }

    /// <summary>
    /// Squarification is defined only over a decreasing sequence, and a snapshot of a scan still
    /// running is ordered by name so that it stays still. Handed one, this would pack rows out of
    /// rectangles whose sizes it had no order for — a picture that looks like a treemap and is not
    /// one — so it refuses instead.
    /// </summary>
    [Fact]
    public void RefusesATreeThatIsNotOrderedBySize()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("a.bin", IsDirectory: false, IsLink: false, Size: 100),
            new ExploreChild("b.bin", IsDirectory: false, IsLink: false, Size: 900),
        ]);

        var tree = builder.Build(ExploreChildOrder.ByName);

        Assert.Throws<ArgumentException>(
            () => TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default));
    }

    private static ExploreTree TreeOf(params long[] sizes)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. sizes.Select((size, i) => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, size))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>A chain of directories, each holding one file and one directory.</summary>
    private static ExploreTree NestedTree(int depth)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var parent = ExploreTreeBuilder.RootNode;

        for (var i = 0; i < depth; i++)
        {
            var first = builder.AddChildren(parent, [
                new ExploreChild($"dir{i}", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000),
            ]);

            parent = first;
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
