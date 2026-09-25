using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The frame a treemap draws round a folder: a band along its top for its name, and a gap down its
/// sides and along its bottom, as wide as the spacing setting asks. It is what shows where one
/// folder ends and the next begins, so these assert where the children sit inside it.
/// </summary>
public sealed class TreemapFrameTests
{
    private const float Width = 800;
    private const float Height = 600;

    /// <summary>A hundredth of a pixel, for the single-precision edges a squarified row produces.</summary>
    private const float Hair = 0.01f;

    [Fact]
    public void AFolderWithRoomForItsNameIsGivenABand()
    {
        var tree = TwoFolders();
        var limits = LayoutLimits.Default;

        var big = TileOf(Layout(tree, limits), tree, "big");

        Assert.Equal(limits.HeaderHeight, big.Header);
    }

    [Theory]
    [InlineData(ExploreSpacing.Dense)]
    [InlineData(ExploreSpacing.Comfortable)]
    [InlineData(ExploreSpacing.Spacious)]
    public void AFoldersChildrenSitBelowItsBandAndInsideItsGap(ExploreSpacing spacing)
    {
        var tree = TwoFolders();
        var limits = LayoutLimits.Default.Spaced(spacing);
        var tiles = Layout(tree, limits);
        var big = TileOf(tiles, tree, "big");
        var gap = limits.ContainerGap;

        var children = tiles.Where(tile => tile.IsNode && tree.ParentOf(tile.Node) == big.Node).ToList();

        Assert.NotEmpty(children);

        foreach (var child in children)
        {
            Assert.True(child.Y >= big.Y + big.Header - Hair, "a child runs into its folder's band");
            Assert.True(child.X >= big.X + gap - Hair, "a child runs into its folder's left gap");
            Assert.True(child.X + child.Width <= big.X + big.Width - gap + Hair, "a child runs into the right gap");
            Assert.True(child.Y + child.Height <= big.Y + big.Height - gap + Hair, "a child runs into the bottom gap");
        }

        // And the gap is the one asked for, not merely at least that: the children start on it.
        Assert.Equal(big.X + gap, children.Min(child => child.X), 0.01f);
        Assert.Equal(big.Y + big.Header, children.Min(child => child.Y), 0.01f);
    }

    /// <summary>
    /// The setting has to change the picture. A spacious frame leaves more room round a folder's
    /// contents than a dense one, and so less for them.
    /// </summary>
    [Fact]
    public void AWiderSpacingLeavesLessRoomForTheContents()
    {
        var tree = TwoFolders();

        double Covered(ExploreSpacing spacing)
        {
            var tiles = Layout(tree, LayoutLimits.Default.Spaced(spacing));
            return tiles.Where(tile => tile.Depth == 2).Sum(tile => (double)tile.Width * tile.Height);
        }

        Assert.True(Covered(ExploreSpacing.Dense) > Covered(ExploreSpacing.Comfortable));
        Assert.True(Covered(ExploreSpacing.Comfortable) > Covered(ExploreSpacing.Spacious));
    }

    /// <summary>
    /// A folder too short for two bands' height gets no band, because it would be all name and no
    /// contents. It keeps the gap on all four sides instead.
    /// </summary>
    [Fact]
    public void AFolderTooShortForABandIsFramedByTheGapAlone()
    {
        var tree = TwoFolders();
        var limits = LayoutLimits.Default;
        var height = limits.HeaderHeight * 2;

        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, height, limits);
        var root = tiles.Single(tile => tile.Node == tree.RootNode);

        Assert.Equal(0, root.Header);
        Assert.Equal(limits.ContainerGap, tiles.Where(tile => tile.Depth == 1).Min(tile => tile.Y), 0.01f);
    }

    /// <summary>
    /// The setting reaches the drawing, not only the layout: a point just inside the edge is the
    /// root's own gap at Spacious and already a folder at Dense.
    /// </summary>
    [Fact]
    public void TheDrawingUsesTheSpacingItWasGiven()
    {
        var tree = TwoFolders();

        int NodeAt(ExploreSpacing spacing)
        {
            var hit = ExploreSurface.Create(
                    tree, tree.RootNode, ExploreView.Treemap, (int)Width, (int)Height, scale: 1, textScale: 1,
                    ShapeColours.ByBranch(ExploreScheme.Standard), spacing, VolumeSpace.None)
                .At(4.5f, Height / 2);

            Assert.NotNull(hit);
            return hit.Value.Node;
        }

        Assert.Equal(tree.RootNode, NodeAt(ExploreSpacing.Spacious));
        Assert.NotEqual(tree.RootNode, NodeAt(ExploreSpacing.Dense));
    }

    /// <summary>
    /// §6.5: a folder's name is a text control that grows with the reader's Windows text size, so
    /// the band it sits in grows with it. A band kept at its 100% height under 150% text would let
    /// the name spill onto the shapes below, whose colours its own was not chosen against.
    /// </summary>
    [Fact]
    public void TheBandGrowsWithTheReadersTextSize()
    {
        var tree = TwoFolders();

        int NodeAt(double textScale, float y)
        {
            var hit = ExploreSurface.Create(
                    tree, tree.RootNode, ExploreView.Treemap, (int)Width, (int)Height, scale: 1, textScale,
                    ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None)
                .At(Width / 2, y);

            Assert.NotNull(hit);
            return hit.Value.Node;
        }

        var limits = LayoutLimits.Default.ForText(1.5);

        Assert.Equal(LayoutLimits.Default.MinimumLabelHeight * 1.5f, limits.MinimumLabelHeight, 0.01f);

        // The root's band is the first thing inside the canvas, so its children start where it ends.
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits);

        Assert.Equal(limits.HeaderHeight, tiles.Where(tile => tile.Depth == 1).Min(tile => tile.Y), 0.01f);
        Assert.True(limits.HeaderHeight > LayoutLimits.Default.HeaderHeight);

        // And the drawing is laid out with it: just below a 100% band is still the root's own band
        // at 150%, and already a folder at 100%.
        var below = LayoutLimits.Default.HeaderHeight + 2;

        Assert.Equal(tree.RootNode, NodeAt(1.5, below));
        Assert.NotEqual(tree.RootNode, NodeAt(1, below));
    }

    /// <summary>A band is for a folder's name above its contents, so nothing without contents has one.</summary>
    [Fact]
    public void AFileIsNeverGivenABand()
    {
        var tree = TwoFolders();

        Assert.All(
            Layout(tree, LayoutLimits.Default).Where(tile => tile.IsNode && !tree.IsDirectory(tile.Node)),
            file => Assert.Equal(0, file.Header));
    }

    /// <summary>
    /// A folder at the depth limit is drawn as one block, so it has no contents to put a band over.
    /// A band there would name a folder whose frame is empty.
    /// </summary>
    [Fact]
    public void AFolderAtTheDepthLimitIsGivenNoBand()
    {
        var tree = TwoFolders();
        var limits = LayoutLimits.Default with { MaximumDepth = 1 };

        Assert.Equal(0, TileOf(Layout(tree, limits), tree, "big").Header);
    }

    /// <summary>
    /// The band is flat. A cushion is brightest along a shape's top-left and darkest at its
    /// top-right, and the name's colour is chosen to contrast with the folder's colour, so a cushion
    /// under the band would put the text on a colour it was not chosen for.
    /// </summary>
    [Fact]
    public void AFoldersBandIsPaintedInItsOwnColourFromEndToEnd()
    {
        var tree = TwoFolders();
        var surface = ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Treemap, (int)Width, (int)Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None);
        var big = TileOf(Layout(tree, LayoutLimits.Default), tree, "big");

        var pixels = new byte[PixelBuffer.LengthFor((int)Width, (int)Height)];
        surface.Paint(pixels, new TileColour(0, 0, 0));

        var y = (int)(big.Y + (big.Header / 2));
        var left = At(pixels, (int)big.X + 2, y);
        var right = At(pixels, (int)(big.X + big.Width) - 3, y);

        Assert.Equal(left, right);

        // A shape with no band is still cushioned, so this is the band's rule and not a flat map.
        var file = Layout(tree, LayoutLimits.Default).First(tile => tile.IsNode && !tree.IsDirectory(tile.Node));
        var middle = (int)(file.Y + (file.Height / 2));

        Assert.NotEqual(At(pixels, (int)file.X + 1, middle), At(pixels, (int)(file.X + file.Width) - 2, middle));
    }

    private static IReadOnlyList<ExploreTile> Layout(ExploreTree tree, LayoutLimits limits) =>
        TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits);

    private static ExploreTile TileOf(IReadOnlyList<ExploreTile> tiles, ExploreTree tree, string name) =>
        tiles.Single(tile => tile.IsNode && tree.NameOf(tile.Node) == name);

    private static TileColour At(byte[] pixels, int x, int y)
    {
        var offset = ((y * (int)Width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    /// <summary>Two folders of files under the root, the larger holding four and the smaller two.</summary>
    private static ExploreTree TwoFolders()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var folders = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("big", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("small", IsDirectory: true, IsLink: false, Size: 0),
            ]);

        builder.AddChildren(
            folders,
            [.. Enumerable.Range(0, 4).Select(i =>
                new ExploreChild($"big{i}", IsDirectory: false, IsLink: false, Size: 4000 - (i * 500)))]);

        builder.AddChildren(
            folders + 1,
            [.. Enumerable.Range(0, 2).Select(i =>
                new ExploreChild($"small{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
