using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// How far the map can zoom: above the zoom on screen while what is in view holds detail the drawing
/// could not draw one by one, and where the zoom is once everything in view is drawn.
/// </summary>
public sealed class MapCeilingTests
{
    private const int Width = 800;
    private const int Height = 600;

    [Theory]
    [InlineData(1, 1, MapCeiling.Least)]
    [InlineData(100, 1, 100)]
    [InlineData(10, 50, 500)]
    [InlineData(1e8, 100, MapCeiling.Most)]
    public void TheCeilingIsTheZoomTheUndrawnDetailNeedsHeldToItsLimits(double zoom, double deeper, double expected) =>
        Assert.Equal(expected, MapCeiling.Of(zoom, deeper));

    /// <summary>
    /// An item a tenth of a pixel across is drawn once it is twice the smallest shape across; one that
    /// is already that wide, held back by a frame or a row, still gets one more level of detail.
    /// </summary>
    [Theory]
    [InlineData(0.1, 3, 60)]
    [InlineData(6, 3, MapCeiling.Step)]
    [InlineData(40, 3, MapCeiling.Step)]
    public void AnItemIsRevealedOnceItIsTwiceTheSmallestShapeAcross(double finest, double smallest, double expected) =>
        Assert.Equal(expected, MapCeiling.Revealing(finest, smallest), 9);

    /// <summary>
    /// A shape has room for a name once both its sides do, with a pixel to spare: the tighter one
    /// decides.
    /// </summary>
    [Theory]
    [InlineData(7, 17, 7)]
    [InlineData(98, 10, 1.7)]
    [InlineData(196, 50, 0.34)]
    public void AShapeIsNamedOnceItsTighterSideHasRoom(double width, double height, double expected) =>
        Assert.Equal(expected, MapCeiling.Naming(width, height, labelWidth: 48, labelHeight: 16), 9);

    /// <summary>
    /// A long tail of tiny files beside one huge one is still a block standing in for them at the
    /// least ceiling. Zoomed into it to each drawing's ceiling in turn, the ceiling rises above the
    /// zoom while the block is in view, each zoom draws files the one before stood in for, and once
    /// every file in view is drawn and has room for its name the ceiling stops where the zoom is.
    /// </summary>
    [Fact]
    public void TheCeilingRisesWhileThereIsDetailInViewAndStopsWhereItEnds()
    {
        var tree = LongTail();
        var (x, y) = CentreOf(Layout(tree, MapViewport.Whole).Single(tile => tile.IsAggregate), MapViewport.Whole);
        var viewport = MapViewport.Anchored(MapCeiling.Least, x, y, 0.5, 0.5, MapCeiling.Least);
        var tiles = Layout(tree, viewport);

        Assert.Contains(tiles, tile => tile.IsAggregate && OnCanvas(tile));

        for (var drawings = 0; tiles.Any(tile => tile.IsAggregate && OnCanvas(tile)); drawings++)
        {
            Assert.True(drawings < 12, $"still a block in view after {drawings} drawings, at {viewport.Zoom}");

            var ceiling = Draw(tree, ExploreView.Treemap, viewport).Ceiling!.Value;

            Assert.True(ceiling > viewport.Zoom, $"the ceiling {ceiling} did not rise above the zoom {viewport.Zoom}");

            var deeper = MapViewport.Anchored(ceiling, x, y, 0.5, 0.5, ceiling);
            var next = Layout(tree, deeper);

            Assert.True(
                Files(next) > Files(tiles),
                $"zoomed to {ceiling}, {Files(next)} files were drawn one by one, against {Files(tiles)} at {viewport.Zoom}");

            (viewport, tiles) = (deeper, next);
        }

        // Every file in view is drawn, the smallest of them too small yet to name: the ceiling goes as
        // far as gives each room for its name, and there it stops.
        var naming = Draw(tree, ExploreView.Treemap, viewport).Ceiling!.Value;

        Assert.Contains(tiles, tile => tile.IsNode && OnCanvas(tile) && !tile.HasRoomForALabel(LayoutLimits.Default));
        Assert.True(naming > viewport.Zoom, $"the ceiling {naming} did not rise to give the files in view room for a name");

        var named = MapViewport.Anchored(naming, x, y, 0.5, 0.5, naming);

        Assert.All(
            Layout(tree, named).Where(tile => tile.IsNode && OnCanvas(tile)),
            tile => Assert.True(tile.HasRoomForALabel(LayoutLimits.Default), $"{tile} has no room for a name"));
        Assert.Equal(named.Zoom, Draw(tree, ExploreView.Treemap, named).Ceiling!.Value, named.Zoom * 1e-9);
    }

    /// <summary>
    /// A folder drawn as one block, past the depth this zoom opens, stands for what it holds as much as
    /// an aggregate does. With no aggregate in view, it alone lifts the ceiling above the zoom, and at
    /// that ceiling what it holds is drawn.
    /// </summary>
    [Fact]
    public void AFolderDrawnAsOneBlockRaisesTheCeiling()
    {
        var tree = NestedFolders(30);
        var viewport = MapViewport.Anchored(MapCeiling.Least, 0.5, 0.5, 0.5, 0.5, MapCeiling.Least);
        var tiles = Layout(tree, viewport);
        var blocks = tiles.Where(tile => IsUnopenedFolder(tree, tile, tiles) && OnCanvas(tile)).ToList();

        Assert.NotEmpty(blocks);
        Assert.DoesNotContain(tiles, tile => tile.IsAggregate && OnCanvas(tile));

        var ceiling = Draw(tree, ExploreView.Treemap, viewport).Ceiling!.Value;

        Assert.True(ceiling > viewport.Zoom, $"the ceiling {ceiling} did not rise above the zoom on screen");

        var deeper = Layout(tree, MapViewport.Anchored(ceiling, 0.5, 0.5, 0.5, 0.5, ceiling));

        Assert.Contains(deeper, tile => tile.IsNode && OnCanvas(tile) && blocks.Any(block => tree.ParentOf(tile.Node) == block.Node));
    }

    /// <summary>A drawing that draws every shape in view holds the ceiling where its zoom is, above the least.</summary>
    [Fact]
    public void ADrawingOfEveryShapeHoldsTheCeilingAtItsZoom()
    {
        var tree = FilesOf(400, 300, 200, 100);
        var deep = MapViewport.Anchored(100, 0.3, 0.3, 0.5, 0.5, 1000);

        Assert.DoesNotContain(Layout(tree, deep), tile => tile.IsAggregate);
        Assert.Equal(100, Draw(tree, ExploreView.Treemap, deep).Ceiling!.Value, 9);
        Assert.Equal(MapCeiling.Least, Draw(tree, ExploreView.Treemap, MapViewport.Whole).Ceiling);
    }

    /// <summary>Detail the drawing left undrawn off the screen is not in view, and does not lift the ceiling.</summary>
    [Fact]
    public void UndrawnDetailOffTheScreenLeavesTheCeilingAtTheZoom()
    {
        var tree = LongTail();
        var aggregate = Layout(tree, MapViewport.Whole).Single(tile => tile.IsAggregate);
        var (x, y) = CentreOf(aggregate, MapViewport.Whole);

        // The far side of the picture from the tail, which the huge file fills.
        var away = MapViewport.Anchored(100, 1 - x, 1 - y, 0.5, 0.5, 1000);

        Assert.DoesNotContain(Layout(tree, away), tile => tile.IsAggregate && OnCanvas(tile));
        Assert.Equal(100, Draw(tree, ExploreView.Treemap, away).Ceiling!.Value, 9);
    }

    /// <summary>
    /// What a block needs is decided by the smallest item it stands for: the same tail with one item
    /// ten times smaller among them needs a deeper ceiling, though its largest item is the same.
    /// </summary>
    [Fact]
    public void TheSmallestItemABlockStandsForDecidesTheCeiling()
    {
        var even = FilesOf([10_000_000_000, .. Enumerable.Repeat(10L, 100_000)]);
        var uneven = FilesOf([10_000_000_000, .. Enumerable.Repeat(10L, 100_000), 1]);
        var (x, y) = CentreOf(Layout(even, MapViewport.Whole).Single(tile => tile.IsAggregate), MapViewport.Whole);
        var viewport = MapViewport.Anchored(MapCeiling.Least, x, y, 0.5, 0.5, MapCeiling.Least);

        Assert.Contains(Layout(even, viewport), tile => tile.IsAggregate);
        Assert.Contains(Layout(uneven, viewport), tile => tile.IsAggregate);

        var evenCeiling = Draw(even, ExploreView.Treemap, viewport).Ceiling!.Value;
        var unevenCeiling = Draw(uneven, ExploreView.Treemap, viewport).Ceiling!.Value;

        Assert.True(evenCeiling > viewport.Zoom, $"the ceiling {evenCeiling} did not rise above the zoom on screen");
        Assert.True(
            unevenCeiling > evenCeiling * 2,
            $"a block with an item ten times smaller in it allowed {unevenCeiling}, against {evenCeiling} without");
    }

    /// <summary>
    /// A drawing says how far the map can zoom exactly where it can be zoomed at all: the map limits
    /// the zoom of every drawing with a viewport by its ceiling, and has nothing to limit it by without.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap, true)]
    [InlineData(ExploreView.Icicle, false)]
    [InlineData(ExploreView.Sunburst, false)]
    public void ADrawingHasACeilingExactlyWhereItHasAViewport(ExploreView view, bool zooms)
    {
        foreach (var viewport in new[] { MapViewport.Whole, MapViewport.Anchored(100, 0.3, 0.3, 0.5, 0.5, 1000) })
        {
            var drawing = Draw(LongTail(), view, viewport);

            Assert.Equal(zooms, drawing.Viewport is not null);
            Assert.Equal(zooms, drawing.Ceiling is not null);
        }
    }

    private static ExploreSurface Draw(ExploreTree tree, ExploreView view, MapViewport viewport) =>
        ExploreSurface.Create(
            tree, tree.RootNode, view, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None, viewport);

    /// <summary>The tiles <see cref="Draw"/> lays out for a treemap, which its limits are the same as.</summary>
    private static IReadOnlyList<ExploreTile> Layout(ExploreTree tree, MapViewport viewport) =>
        TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, viewport: viewport);

    private static int Files(IReadOnlyList<ExploreTile> tiles) => tiles.Count(tile => tile.IsNode && OnCanvas(tile));

    private static bool OnCanvas(ExploreTile tile) =>
        tile.X < Width && tile.X + tile.Width > 0 && tile.Y < Height && tile.Y + tile.Height > 0;

    /// <summary>Where the middle of <paramref name="tile"/>, drawn at <paramref name="viewport"/>, is in the whole picture.</summary>
    private static (double X, double Y) CentreOf(ExploreTile tile, MapViewport viewport) =>
        viewport.PictureAt((tile.X + (tile.Width / 2)) / Width, (tile.Y + (tile.Height / 2)) / Height);

    private static ExploreTree FilesOf(params long[] sizes)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. sizes.Select((size, i) => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, size))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// One huge file and a hundred thousand of a byte each: a ten-thousandth of the picture, which at
    /// the least ceiling still leaves each of them under the smallest shape drawn.
    /// </summary>
    private static ExploreTree LongTail() =>
        FilesOf([1_000_000_000, .. Enumerable.Repeat(1L, 100_000)]);

    /// <summary>Whether <paramref name="tile"/> is a folder with nothing drawn inside it.</summary>
    private static bool IsUnopenedFolder(ExploreTree tree, ExploreTile tile, IReadOnlyList<ExploreTile> tiles) =>
        tile.IsNode
        && ((ISizedTree)tree).IsContainer(tile.Node)
        && !tiles.Any(inside => inside.IsNode && inside.Node != tile.Node && tree.ParentOf(inside.Node) == tile.Node);

    /// <summary>A chain of folders <paramref name="depth"/> deep, each holding a file and the next.</summary>
    private static ExploreTree NestedFolders(int depth)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var parent = ExploreTreeBuilder.RootNode;

        for (var i = 0; i < depth; i++)
        {
            parent = builder.AddChildren(parent, [
                new ExploreChild($"folder{i}", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 100),
            ]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
