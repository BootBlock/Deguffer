using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which way the map's camera flies for a change of folder, and from which shape: into a folder below
/// however deep, out to one above however far up, out of the volume and back into it, and not at all
/// between folders where neither holds the other.
/// </summary>
public sealed class FolderStepsTests
{
    private const int Width = 1200;

    private const int Height = 800;

    private const double Precision = 1e-9;

    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AFolderBelowIsAStepInAndOneAboveIsAStepOutHoweverFar()
    {
        var tree = Nested();
        var top = Find(tree, "top0");
        var deep = Find(tree, "mid01");

        Assert.Equal(FolderStep.Into, FolderSteps.Between(tree, tree.RootNode, false, top, false));
        Assert.Equal(FolderStep.Into, FolderSteps.Between(tree, tree.RootNode, false, deep, false));
        Assert.Equal(FolderStep.OutOf, FolderSteps.Between(tree, deep, false, top, false));
        Assert.Equal(FolderStep.OutOf, FolderSteps.Between(tree, deep, false, tree.RootNode, false));
    }

    [Fact]
    public void FoldersSideBySideAreNoStepInOrOut()
    {
        var tree = Nested();

        Assert.Equal(FolderStep.Across, FolderSteps.Between(tree, Find(tree, "mid01"), false, Find(tree, "top1"), false));
        Assert.Equal(FolderStep.Across, FolderSteps.Between(tree, Find(tree, "top0"), false, Find(tree, "top0"), false));
    }

    /// <summary>The root opened out of the volume drawn beside it is a step in, and going back to the volume a step out.</summary>
    [Fact]
    public void TheRootOpensOutOfItsVolumeAndClosesBackIntoIt()
    {
        var tree = Nested();

        Assert.Equal(FolderStep.Into, FolderSteps.Between(tree, tree.RootNode, true, tree.RootNode, false));
        Assert.Equal(FolderStep.OutOf, FolderSteps.Between(tree, tree.RootNode, false, tree.RootNode, true));
        Assert.Equal(FolderStep.OutOf, FolderSteps.Between(tree, Find(tree, "top2"), false, tree.RootNode, true));
    }

    /// <summary>A folder drawn one by one is flown into from its own shape.</summary>
    [Fact]
    public void AFolderDrawnIsFlownToFromItsOwnShape()
    {
        var tree = Nested();
        var top = Find(tree, "top1");
        var drawing = Treemap(tree);
        var tile = drawing.Outlines(new HashSet<int> { top }).Single().Points;

        var frame = drawing.FrameOf(top)!.Value;

        // The outline's corners are in single precision, as the shapes are.
        const double Pixel = 1e-6;

        Assert.Equal(tile[0].X / (double)Width, frame.X, Pixel);
        Assert.Equal(tile[0].Y / (double)Height, frame.Y, Pixel);
        Assert.Equal((tile[2].X - tile[0].X) / (double)Width, frame.Width, Pixel);
        Assert.Equal((tile[2].Y - tile[0].Y) / (double)Height, frame.Height, Pixel);
    }

    /// <summary>
    /// A folder too deep for the drawing to draw on its own is flown to from the deepest folder above
    /// it that is drawn, which is where the screen shows it.
    /// </summary>
    [Fact]
    public void AFolderNotDrawnIsFlownToFromTheDeepestFolderAboveItThatIs()
    {
        var tree = Chain(depth: 40);
        var drawing = Treemap(tree);
        var deepest = Find(tree, "level39");

        Assert.Empty(drawing.Outlines(new HashSet<int> { deepest }));

        var frame = drawing.FrameOf(deepest)!.Value;
        var drawn = Enumerable.Range(0, 40)
            .Select(level => Find(tree, $"level{level}"))
            .Last(node => drawing.Outlines(new HashSet<int> { node }).Count > 0);

        Assert.Equal(drawing.FrameOf(drawn), frame);
        Assert.NotEqual(tree.RootNode, drawn);
    }

    [Fact]
    public void AFolderOutsideTheDrawingHasNoShapeInIt()
    {
        var tree = Nested();
        var drawing = Treemap(tree, Find(tree, "top0"));

        Assert.Null(drawing.FrameOf(Find(tree, "top3")));
        Assert.Null(drawing.FrameOf(tree.RootNode));
    }

    /// <summary>
    /// The shape flown to is where the inner folder's screen lies on the outer one's: the folder's
    /// shape where the outer picture shows it zoomed, and the part of the inner picture its own zoom
    /// shows, for a folder returned to at the zoom the reader had there.
    /// </summary>
    [Fact]
    public void TheShapeFlownToFollowsBothZooms()
    {
        var tree = Nested();
        var top = Find(tree, "top1");
        var drawing = Treemap(tree);
        var frame = drawing.FrameOf(top)!.Value;

        Assert.Equal(frame, drawing.ScreenOf(top, MapViewport.Whole, MapViewport.Whole));

        // The outer picture magnified twice about its top-left corner doubles the folder's shape.
        var doubled = drawing.ScreenOf(top, MapViewport.Anchored(2, 0, 0, 0, 0, ceiling: 64), MapViewport.Whole)!.Value;

        AssertClose(new MapFrame(frame.X * 2, frame.Y * 2, frame.Width * 2, frame.Height * 2), doubled);

        // The inner picture magnified four times about its middle shows the middle quarter of the shape.
        var middle = drawing.ScreenOf(top, MapViewport.Whole, MapViewport.Anchored(4, 0.5, 0.5, 0.5, 0.5, ceiling: 64))!.Value;

        AssertClose(
            new MapFrame(frame.X + (frame.Width * 0.375), frame.Y + (frame.Height * 0.375), frame.Width / 4, frame.Height / 4),
            middle);
    }

    private static void AssertClose(MapFrame expected, MapFrame actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }

    [Fact]
    public void ADrawingThatCannotZoomHasNoShapeToFlyTo()
    {
        var tree = Nested();
        var sunburst = ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Sunburst, Width, Height, scale: 1, textScale: 1,
            ExploreColouring.Branch, ExploreScheme.Standard, Now, ExploreSpacing.Comfortable, VolumeSpace.None);

        Assert.Null(sunburst.ScreenOf(Find(tree, "top0"), MapViewport.Whole, MapViewport.Whole));
    }

    private static ExploreSurface Treemap(ExploreTree tree, int? root = null) =>
        ExploreSurface.Create(
            tree, root ?? tree.RootNode, ExploreView.Treemap, Width, Height, scale: 1, textScale: 1,
            ExploreColouring.Branch, ExploreScheme.Standard, Now, ExploreSpacing.Comfortable, VolumeSpace.None);

    private static int Find(ExploreTree tree, string name)
    {
        var pending = new Stack<int>([tree.RootNode]);

        while (pending.TryPop(out var node))
        {
            if (tree.NameOf(node) == name)
            {
                return node;
            }

            foreach (var child in tree.ChildrenOf(node))
            {
                pending.Push(child);
            }
        }

        throw new InvalidOperationException($"No node is named {name}.");
    }

    /// <summary>Four folders under the root, each with two folders of three files.</summary>
    private static ExploreTree Nested()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var top = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, 4).Select(i => new ExploreChild($"top{i}", IsDirectory: true, IsLink: false, Size: 0))]);

        for (var i = 0; i < 4; i++)
        {
            var middle = builder.AddChildren(
                top + i,
                [.. Enumerable.Range(0, 2).Select(j => new ExploreChild($"mid{i}{j}", IsDirectory: true, IsLink: false, Size: 0))]);

            for (var j = 0; j < 2; j++)
            {
                builder.AddChildren(
                    middle + j,
                    [.. Enumerable.Range(0, 3).Select(k =>
                        new ExploreChild($"f{i}{j}{k}", IsDirectory: false, IsLink: false, Size: ((4 - i) * 100_000) + k))]);
            }
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// A chain of <paramref name="depth"/> folders, one inside the next, each beside a file as large
    /// as the rest of the chain, so every level halves the room and the last ones are too small to draw.
    /// </summary>
    private static ExploreTree Chain(int depth)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var parent = ExploreTreeBuilder.RootNode;

        for (var level = 0; level < depth; level++)
        {
            var size = 1L << (depth - level);
            var first = builder.AddChildren(
                parent,
                [
                    new ExploreChild($"level{level}", IsDirectory: true, IsLink: false, Size: 0),
                    new ExploreChild($"beside{level}", IsDirectory: false, IsLink: false, Size: size),
                ]);

            parent = first;
        }

        builder.AddChildren(parent, [new ExploreChild("last", IsDirectory: false, IsLink: false, Size: 2)]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
