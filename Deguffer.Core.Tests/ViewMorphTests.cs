using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A change of view carries the largest shapes from where one view draws them to where the other does,
/// each traced the same way in both, and no more of them than the limit.
/// </summary>
public sealed class ViewMorphTests
{
    private const int Width = 1200;

    private const int Height = 800;

    /// <summary>
    /// Each shape starts on its own shape in the view left and ends on its own shape in the view
    /// arrived at: the middle of each outline is over that node, or something inside it, in its view.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap, ExploreView.Sunburst)]
    [InlineData(ExploreView.Sunburst, ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle, ExploreView.Sunburst)]
    [InlineData(ExploreView.Treemap, ExploreView.Icicle)]
    public void EachShapeTravelsFromItsShapeInOneViewToItsShapeInTheOther(ExploreView from, ExploreView to)
    {
        var tree = Many(folders: 6, files: 5);
        var earlier = Drawing(tree, from);
        var later = Drawing(tree, to);

        var morphs = ViewMorph.Between(earlier, later);

        Assert.NotEmpty(morphs);

        foreach (var morph in morphs)
        {
            Assert.Equal(MorphOutline.Points, morph.From.Count);
            Assert.Equal(MorphOutline.Points, morph.To.Count);
            AssertOver(earlier, morph.Node, morph.From);
            AssertOver(later, morph.Node, morph.To);
        }
    }

    /// <summary>
    /// An icicle's bar becomes its wedge the way round the reader expects: its top, the side nearest
    /// the root, becomes the wedge's inner arc, and its left where the wedge starts.
    /// </summary>
    [Fact]
    public void ABarsTopBecomesTheInnerArcOfItsWedge()
    {
        var tree = Many(folders: 3, files: 2);
        var icicle = Drawing(tree, ExploreView.Icicle);
        var morphs = ViewMorph.Between(icicle, Drawing(tree, ExploreView.Sunburst));
        var folder = Assert.Single(morphs, morph => tree.NameOf(morph.Node) == "folder00");
        var bar = Assert.Single(icicle.Tiles, tile => tile.Node == folder.Node);

        Assert.Equal(new ExplorePoint(bar.X, bar.Y), folder.From[0]);
        Assert.Equal(new ExplorePoint(bar.X + bar.Width, bar.Y), folder.From[MorphOutline.Steps]);
        Assert.Equal(new ExplorePoint(bar.X, bar.Y + bar.Height), folder.From[^1]);

        // The sunburst is drawn about the middle of the canvas, and its wedges go clockwise from
        // twelve o'clock. The inner arc's start is nearer the middle than the outer arc's.
        var inner = Distance(folder.To[0]);
        var outer = Distance(folder.To[^1]);

        Assert.True(inner < outer, $"the inner arc is at {inner} and the outer at {outer}");
        Assert.Equal(inner, Distance(folder.To[MorphOutline.Steps]), 0.01);
        Assert.Equal(outer, Distance(folder.To[MorphOutline.Steps + 1]), 0.01);
    }

    /// <summary>
    /// Only as many shapes as the limit travel, and they are the largest the two views both draw, so
    /// the work of a frame is bounded whatever the tree holds.
    /// </summary>
    [Fact]
    public void OnlyTheLargestShapesBothViewsDrawTravel()
    {
        var tree = Nested(breadth: 6);
        var earlier = Drawing(tree, ExploreView.Treemap);
        var later = Drawing(tree, ExploreView.Icicle);
        var both = earlier.NodesDrawn.Intersect(later.NodesDrawn).ToHashSet();

        Assert.True(both.Count > ViewMorph.MostShapes, $"the views both draw only {both.Count} shapes");

        var morphs = ViewMorph.Between(earlier, later);
        var travelling = morphs.Select(morph => morph.Node).ToHashSet();

        Assert.Equal(ViewMorph.MostShapes, morphs.Count);
        Assert.Subset(both, travelling);
        Assert.True(
            travelling.Min(tree.SizeOf) >= both.Except(travelling).Max(tree.SizeOf),
            "a shape left to crossfade is larger than one that travels");
    }

    /// <summary>
    /// A shape the treemap draws inside another is painted over it as they travel, so the folder does
    /// not cover what it holds.
    /// </summary>
    [Fact]
    public void AFolderIsPaintedBeforeWhatItHolds()
    {
        var tree = Many(folders: 4, files: 3);
        var morphs = ViewMorph.Between(Drawing(tree, ExploreView.Treemap), Drawing(tree, ExploreView.Sunburst));
        var order = morphs.Select((morph, index) => (morph.Node, index)).ToDictionary(pair => pair.Node, pair => pair.index);
        var nested = 0;

        foreach (var (node, index) in order)
        {
            if (order.TryGetValue(tree.ParentOf(node), out var parent) && tree.ParentOf(node) != node)
            {
                nested++;
                Assert.True(parent < index, $"{tree.NameOf(node)} is painted before its folder");
            }
        }

        Assert.True(nested > 0, "no shape travelling is inside another that travels");
    }

    /// <summary>A shape keeps the colour the view it goes to paints it in, all the way.</summary>
    [Fact]
    public void AShapeIsPaintedInTheColourOfTheViewItGoesTo()
    {
        var tree = Many(folders: 3, files: 2);
        var later = Drawing(tree, ExploreView.Sunburst);
        var morphs = ViewMorph.Between(Drawing(tree, ExploreView.Treemap), later);
        var colours = later.MorphShapes(morphs.Select(morph => morph.Node).ToHashSet()).ToDictionary(shape => shape.Node, shape => shape.Colour);

        Assert.All(morphs, morph => Assert.Equal(colours[morph.Node], morph.Colour));
    }

    [Fact]
    public void NothingTravelsBetweenTwoTrees()
    {
        var earlier = Drawing(Many(folders: 3, files: 2), ExploreView.Treemap);
        var later = Drawing(Many(folders: 3, files: 2), ExploreView.Sunburst);

        Assert.Empty(ViewMorph.Between(earlier, later));
    }

    [Fact]
    public void NothingTravelsBetweenTwoCanvases()
    {
        var tree = Many(folders: 3, files: 2);
        var earlier = Drawing(tree, ExploreView.Treemap);
        var later = ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Sunburst, Width / 2, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None);

        Assert.Empty(ViewMorph.Between(earlier, later));
    }

    /// <summary>
    /// A treemap zoomed into is a part of its picture, and a view changes between whole pictures, so
    /// nothing travels from it: the shapes on screen are not where the whole treemap draws them.
    /// </summary>
    [Fact]
    public void NothingTravelsFromAZoomedTreemap()
    {
        var tree = Many(folders: 3, files: 2);
        var zoomed = ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Treemap, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None,
            MapViewport.Seen(4, 0.25, 0.25));

        Assert.False(zoomed.Viewport!.Value.IsWhole);
        Assert.NotEmpty(ViewMorph.Between(Drawing(tree, ExploreView.Treemap), Drawing(tree, ExploreView.Icicle)));
        Assert.Empty(ViewMorph.Between(zoomed, Drawing(tree, ExploreView.Icicle)));
    }

    [Theory]
    [InlineData(ExploreView.Treemap, ExploreView.Icicle, true)]
    [InlineData(ExploreView.Icicle, ExploreView.Sunburst, true)]
    [InlineData(ExploreView.Sunburst, ExploreView.Treemap, true)]
    [InlineData(ExploreView.Treemap, ExploreView.Treemap, false)]
    [InlineData(ExploreView.Treemap, ExploreView.List, false)]
    [InlineData(ExploreView.Tree, ExploreView.Sunburst, false)]
    [InlineData(ExploreView.Files, ExploreView.Icicle, false)]
    public void OnlyAChangeBetweenTwoPicturesMorphs(ExploreView from, ExploreView to, bool morphs) =>
        Assert.Equal(morphs, ViewMorph.Morphs(Many(folders: 2, files: 2), from, to));

    /// <summary>
    /// A scan still running is drawn as an icicle whichever view is picked, so a change of view during
    /// one leaves the same picture on the map, and nothing travels.
    /// </summary>
    [Fact]
    public void AChangeOfViewDuringAScanMorphsNothing()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, [new ExploreChild("a.bin", IsDirectory: false, IsLink: false, Size: 1000)]);
        var scanning = builder.Build(ExploreChildOrder.ByName);

        Assert.False(ViewMorph.Morphs(scanning, ExploreView.Treemap, ExploreView.Sunburst));
    }

    [Fact]
    public void WithMotionOffTheViewsSwapAtOnce()
    {
        var motion = MotionToken.Morph.For(animationsEnabled: false);

        Assert.True(motion.IsInstant);
        Assert.False(motion.Travels);
        Assert.True(MotionToken.Morph.For(animationsEnabled: true).Travels);
    }

    /// <summary>
    /// The middle of <paramref name="outline"/>, half way along its edges and half way between them,
    /// is over <paramref name="node"/> in <paramref name="drawing"/>: the node itself, or a shape the
    /// treemap draws inside it there.
    /// </summary>
    private static void AssertOver(ExploreSurface drawing, int node, IReadOnlyList<ExplorePoint> outline)
    {
        var inner = outline[MorphOutline.Steps / 2];
        var outer = outline[MorphOutline.Points - 1 - (MorphOutline.Steps / 2)];
        var hit = drawing.At((inner.X + outer.X) / 2, (inner.Y + outer.Y) / 2);

        Assert.NotNull(hit);

        var tree = drawing.Tree;

        for (var at = hit.Value.Node; ; at = tree.ParentOf(at))
        {
            if (at == node)
            {
                return;
            }

            Assert.False(at == tree.ParentOf(at), $"the middle of node {node}'s outline is over node {hit.Value.Node}");
        }
    }

    private static double Distance(ExplorePoint point) =>
        Math.Sqrt(Math.Pow(point.X - (Width / 2.0), 2) + Math.Pow(point.Y - (Height / 2.0), 2));

    private static ExploreSurface Drawing(ISizedTree tree, ExploreView view) =>
        ExploreSurface.Create(
            tree, tree.RootNode, view, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None);

    /// <summary>
    /// Three levels of <paramref name="breadth"/> each, folders of folders of files of about one size,
    /// so the files are wide enough for an icicle to draw each of them, and every size is different.
    /// </summary>
    private static ExploreTree Nested(int breadth)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var top = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, breadth).Select(i => new ExploreChild($"top{i}", IsDirectory: true, IsLink: false, Size: 0))]);
        var file = 0;

        for (var i = 0; i < breadth; i++)
        {
            var middle = builder.AddChildren(
                top + i,
                [.. Enumerable.Range(0, breadth).Select(k => new ExploreChild($"middle{i}{k}", IsDirectory: true, IsLink: false, Size: 0))]);

            for (var k = 0; k < breadth; k++)
            {
                builder.AddChildren(
                    middle + k,
                    [.. Enumerable.Range(0, breadth).Select(_ => new ExploreChild($"file{file}", IsDirectory: false, IsLink: false, Size: 1_000_000L - (++file * 100L)))]);
            }
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// <paramref name="folders"/> folders of <paramref name="files"/> files, every size different so
    /// the largest are one set whichever way a tie would be broken.
    /// </summary>
    private static ExploreTree Many(int folders, int files)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, folders).Select(i => new ExploreChild($"folder{i:d2}", IsDirectory: true, IsLink: false, Size: 0))]);

        for (var i = 0; i < folders; i++)
        {
            builder.AddChildren(
                first + i,
                [.. Enumerable.Range(0, files).Select(k => new ExploreChild($"file{i:d2}-{k:d2}", IsDirectory: false, IsLink: false, Size: ((folders - i) * 1_000_000L) + ((files - k) * 1_000L)))]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
