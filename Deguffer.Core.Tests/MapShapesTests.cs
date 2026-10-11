using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The shapes a drawing draws in one folder, which the keyboard moves between and a screen reader
/// lists: what the folder holds with a shape of its own, largest first, then its block of the rest,
/// each where the drawing draws it. A treemap that can be zoomed gives every one of them, on the
/// canvas or off it.
/// </summary>
public sealed class MapShapesTests
{
    private const int Width = 800;
    private const int Height = 600;

    private const double Near = 1e-4;

    /// <summary>
    /// At the top of a treemap, the root's own shapes and its block of the rest, in the order laid, each
    /// where the drawing draws it. The fixture has a tail long enough to aggregate, or this proves
    /// nothing about the block.
    /// </summary>
    [Fact]
    public void TheTopOfATreemapIsWhatTheRootHoldsWhereItIsDrawn()
    {
        var tree = Tree();
        var drawing = Drawn(tree, ExploreView.Treemap);
        var shapes = drawing.Shapes(null);

        Assert.Contains(shapes, shape => shape.Node == ExploreTile.Aggregated);
        Assert.Equal(
            [.. drawing.Tiles.Where(tile => tile.Depth == 1).Select(tile => tile.Node).Order()],
            shapes.Select(shape => shape.Node).Order());
        Assert.All(shapes.Where(shape => shape.IsNode), shape => Assert.Equal(tree.RootNode, tree.ParentOf(shape.Node)));
        Assert.All(shapes, shape => AssertDrawnAt(drawing, shape));
        Assert.Equal(shapes.Where(s => s.IsNode).Select(s => s.Bytes), shapes.Where(s => s.IsNode).Select(s => s.Bytes).OrderDescending());
    }

    /// <summary>
    /// A folder's shapes are what it holds, each where the drawing draws it, and none of what is beside
    /// it or deeper inside it.
    /// </summary>
    [Fact]
    public void AFoldersShapesAreWhatItHoldsWhereTheyAreDrawn()
    {
        var tree = Tree();
        var folder = Named(tree, "folder");
        var drawing = Drawn(tree, ExploreView.Treemap);
        var shapes = drawing.Shapes(folder);

        Assert.Equal([.. tree.ChildrenOf(folder)], shapes.Select(shape => shape.Node));
        Assert.All(shapes, shape => AssertDrawnAt(drawing, shape));
    }

    /// <summary>
    /// Beside a volume, the top of the picture is the root's own shape and the blocks of the drive
    /// beside it, and what the root holds is the root's.
    /// </summary>
    [Fact]
    public void BesideAVolumeTheTopIsTheRootAndTheBlocksBesideIt()
    {
        var tree = Tree();
        var drawing = ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Treemap, Width, Height, 1, 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable,
            new VolumeSpace(TotalBytes: 40_000_000, FreeBytes: 20_000_000));

        Assert.True(drawing.HasVolumeBeside);

        var top = drawing.Shapes(null);

        Assert.Equal([tree.RootNode, ExploreTile.FreeSpace, ExploreTile.Unaccounted], top.Select(shape => shape.Node));
        Assert.All(top, shape => AssertDrawnAt(drawing, shape));
        Assert.All(drawing.Shapes(tree.RootNode).Where(shape => shape.IsNode), shape => Assert.Equal(tree.RootNode, tree.ParentOf(shape.Node)));
    }

    /// <summary>
    /// Zoomed in, the shapes the screen has left behind are still there to move to, where
    /// <see cref="ExploreSurface.Locate"/> places them, off the canvas.
    /// </summary>
    [Fact]
    public void AZoomedTreemapGivesTheShapesOffTheCanvasToo()
    {
        var tree = Tree();
        var drawing = ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Treemap, Width, Height, 1, 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None,
            MapViewport.Seen(zoom: 6, left: 0.05, top: 0.05));
        var shapes = drawing.Shapes(null);

        Assert.Contains(shapes, shape => shape.Frame.X > 1 || shape.Frame.Y > 1);
        Assert.All(shapes.Where(shape => shape.IsNode), shape =>
        {
            var located = drawing.Locate(shape.Node)!.Value;

            Assert.Equal(located.X, shape.Frame.X, Near);
            Assert.Equal(located.Y, shape.Frame.Y, Near);
            Assert.Equal(located.Width, shape.Frame.Width, Near);
            Assert.Equal(located.Height, shape.Frame.Height, Near);
        });
    }

    /// <summary>
    /// A folder the picture draws as one block has no shapes in it, and nor has one the picture does not
    /// draw on its own or one outside the drawing's root.
    /// </summary>
    [Fact]
    public void AFolderDrawnAsOneBlockOrNotDrawnHasNone()
    {
        var tree = Tree();
        var drawing = Drawn(tree, ExploreView.Treemap);
        var nook = Named(tree, "nook");
        var inner = ExploreSurface.Create(
            tree, Named(tree, "folder"), ExploreView.Treemap, Width, Height, 1, 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None);

        Assert.Contains(drawing.Tiles, tile => tile.Node == nook && tile.Header == 0);
        Assert.Empty(drawing.Shapes(nook));
        Assert.Empty(drawing.Shapes(Named(tree, "tiny7")));
        Assert.Empty(inner.Shapes(Named(tree, "big0")));
        Assert.NotEmpty(inner.Shapes(null));
    }

    /// <summary>
    /// An icicle's folder holds the shapes in the row under it, including its block of the rest, and not
    /// the shapes of the same row under its neighbours.
    /// </summary>
    [Fact]
    public void AnIciclesFolderHoldsTheRowUnderIt()
    {
        var tree = Tree();
        var folder = Named(tree, "folder");
        var drawing = Drawn(tree, ExploreView.Icicle);
        var top = drawing.Shapes(null);
        var held = drawing.Shapes(folder);

        Assert.Contains(top, shape => shape.Node == ExploreTile.Aggregated);
        Assert.Equal(
            [.. drawing.Tiles.Where(tile => tile.Depth == 1).Select(tile => tile.Node).Order()],
            top.Select(shape => shape.Node).Order());
        Assert.NotEmpty(held);
        Assert.All(held, shape => Assert.Equal(folder, tree.ParentOf(shape.Node)));
        Assert.All(top.Concat(held), shape => AssertDrawnAt(drawing, shape));

        // The row under it holds what its neighbour holds as well, which is not its.
        Assert.Contains(drawing.Tiles, tile => tile.IsNode && tile.Depth == 2 && tree.ParentOf(tile.Node) == Named(tree, "other"));
    }

    /// <summary>
    /// A sunburst's shapes are the wedges in the ring outside the folder's, each standing at a point that
    /// is in its own wedge, so a screen reader's point and the pointer agree on what is there.
    /// </summary>
    [Fact]
    public void ASunburstsShapesStandInsideTheirOwnWedges()
    {
        var tree = Tree();
        var folder = Named(tree, "folder");
        var drawing = Drawn(tree, ExploreView.Sunburst);
        var top = drawing.Shapes(null);
        var held = drawing.Shapes(folder);

        Assert.Contains(top, shape => shape.Node == ExploreTile.Aggregated);
        Assert.All(top.Where(shape => shape.IsNode), shape => Assert.Equal(tree.RootNode, tree.ParentOf(shape.Node)));
        Assert.NotEmpty(held);
        Assert.All(held, shape => Assert.Equal(folder, tree.ParentOf(shape.Node)));

        foreach (var shape in top.Concat(held))
        {
            Assert.Equal(shape.Node, drawing.At((float)(shape.X * Width), (float)(shape.Y * Height))?.Node);
            Assert.InRange(shape.X, shape.Frame.X, shape.Frame.X + shape.Frame.Width);
            Assert.InRange(shape.Y, shape.Frame.Y, shape.Frame.Y + shape.Frame.Height);
        }
    }

    /// <summary>
    /// Every shape the keyboard can be on has an outline to mark it out with, blocks too: a treemap's is
    /// its box, and a sunburst's is the wedge its point is in.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void EveryShapeHasAnOutlineOfItsOwn(ExploreView view)
    {
        var tree = Tree();
        var drawing = Drawn(tree, view);
        var shapes = drawing.Shapes(null);

        Assert.Contains(shapes, shape => !shape.IsNode);

        foreach (var shape in shapes)
        {
            var outlines = drawing.OutlineOf(shape);
            var points = outlines.SelectMany(outline => outline.Points).ToList();

            Assert.NotEmpty(outlines);
            Assert.All(outlines, outline => Assert.Equal(shape.Node, outline.Node));
            Assert.Equal(shape.Frame.X * Width, points.Min(point => point.X), 0.01);
            Assert.Equal((shape.Frame.X + shape.Frame.Width) * Width, points.Max(point => point.X), 0.01);
            Assert.Equal(shape.Frame.Y * Height, points.Min(point => point.Y), 0.01);
            Assert.Equal((shape.Frame.Y + shape.Frame.Height) * Height, points.Max(point => point.Y), 0.01);
        }
    }

    /// <summary>
    /// A wedge wider than half the disc stands at the middle of its arc, which is in it, and not at the
    /// middle of its box, which is over the disc in the middle of the picture.
    /// </summary>
    [Fact]
    public void AWedgeWiderThanHalfTheDiscStandsOnItsArc()
    {
        var builder = new ExploreTreeBuilder(@"C:");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("most", IsDirectory: false, IsLink: false, Size: 3_000),
                new ExploreChild("rest", IsDirectory: false, IsLink: false, Size: 1_000),
            ]);

        var tree = builder.Build(ExploreChildOrder.BySize);
        var drawing = Drawn(tree, ExploreView.Sunburst);
        var most = drawing.Shapes(null).Single(shape => shape.Node == Named(tree, "most"));
        var (boxX, boxY) = most.Frame.Centre;

        Assert.NotEqual(most.Node, drawing.At((float)(boxX * Width), (float)(boxY * Height))?.Node);
        Assert.Equal(most.Node, drawing.At((float)(most.X * Width), (float)(most.Y * Height))?.Node);
    }

    /// <summary>
    /// A shape's box is the tile the drawing draws for it, and the middle of it is over that tile or over
    /// something the drawing draws inside it.
    /// </summary>
    private static void AssertDrawnAt(ExploreSurface drawing, MapShape shape)
    {
        var tile = drawing.Tiles.First(tile => tile.Node == shape.Node && (shape.IsNode || Math.Abs((tile.X / Width) - shape.Frame.X) < Near));

        Assert.Equal(tile.X / Width, shape.Frame.X, Near);
        Assert.Equal(tile.Y / Height, shape.Frame.Y, Near);
        Assert.Equal(tile.Width / Width, shape.Frame.Width, Near);
        Assert.Equal(tile.Height / Height, shape.Frame.Height, Near);

        var inside = drawing.At((float)(shape.X * Width), (float)(shape.Y * Height))!.Value.Node;

        while (inside != shape.Node && inside >= 0 && drawing.Tree.ParentOf(inside) != inside)
        {
            inside = drawing.Tree.ParentOf(inside);
        }

        Assert.Equal(shape.Node, inside);
    }

    private static ExploreSurface Drawn(ExploreTree tree, ExploreView view) => ExploreSurface.Create(
        tree, tree.RootNode, view, Width, Height, 1, 1,
        ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None);

    private static int Named(ExploreTree tree, string name) =>
        Enumerable.Range(0, tree.NodeCount).First(node => tree.NameOf(node) == name);

    /// <summary>
    /// Two folders of files beside three large files, a folder too small to frame, and a long tail of
    /// tiny ones: nesting, a row of what two folders hold side by side, siblings of different sizes, a
    /// folder drawn as one block, and a block of the rest at the top.
    /// </summary>
    private static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var folder = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("folder", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("nook", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("other", IsDirectory: true, IsLink: false, Size: 0),
                .. Enumerable.Range(0, 3).Select(i =>
                    new ExploreChild($"big{i}", IsDirectory: false, IsLink: false, Size: (3 - i) * 1_000_000L)),
                .. Enumerable.Range(0, 500).Select(i =>
                    new ExploreChild($"tiny{i}", IsDirectory: false, IsLink: false, Size: 1)),
            ]);

        builder.AddChildren(
            folder,
            [.. Enumerable.Range(0, 4).Select(i =>
                new ExploreChild($"leaf{i}", IsDirectory: false, IsLink: false, Size: (4 - i) * 500_000L))]);

        builder.AddChildren(
            folder + 1,
            [.. Enumerable.Range(0, 2).Select(i =>
                new ExploreChild($"crumb{i}", IsDirectory: false, IsLink: false, Size: 2_500))]);

        builder.AddChildren(
            folder + 2,
            [.. Enumerable.Range(0, 2).Select(i =>
                new ExploreChild($"part{i}", IsDirectory: false, IsLink: false, Size: 1_500_000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
