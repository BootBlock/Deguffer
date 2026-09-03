using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A surface is one drawing of one node: its geometry, what is under a point, where the text goes,
/// and how it is painted. The four vary together, so the view asks for one of these rather than
/// switching four times — and which shapes are worth labelling becomes a rule that can be tested
/// instead of one that lives in a XAML control (G8).
/// </summary>
public sealed class ExploreSurfaceTests
{
    private const int Width = 800;
    private const int Height = 800;

    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.List)]
    public void EveryViewDrawnInRectanglesAsksForTheSameSurface(ExploreView view)
    {
        var tree = FlatTree(10);

        Assert.IsType<TiledSurface>(
            ExploreSurface.Create(tree, tree.RootNode, view, Width, Height, scale: 1));
    }

    [Fact]
    public void TheSunburstAsksForItsOwn()
    {
        var tree = FlatTree(10);

        Assert.IsType<SunburstSurface>(
            ExploreSurface.Create(tree, tree.RootNode, ExploreView.Sunburst, Width, Height, scale: 1));
    }

    /// <summary>
    /// Only a rectangle with nothing drawn inside it gets a label. A child is inset from its parent
    /// by a single pixel, so a parent's label and its first child's land within two pixels of each
    /// other and overprint into an unreadable stack.
    /// </summary>
    [Fact]
    public void AParentWithSomethingDrawnInsideItIsNotLabelled()
    {
        var tree = NestedTree();
        var labelled = Names(tree, Treemap(tree));

        Assert.DoesNotContain("branch", labelled);
        Assert.Contains("leaf0", labelled);
    }

    /// <summary>
    /// The node being drawn fills the canvas, and the breadcrumb above the picture already names
    /// it. A label for it would sit in the top-left corner over its own first child.
    /// </summary>
    [Fact]
    public void TheNodeBeingDrawnIsNotLabelled()
    {
        // Small enough that nothing inside the root is big enough to draw. The covering rule then
        // has nothing to say about the root, so this rule is the only thing keeping its label off.
        var tree = FlatTree(500);
        var limits = LayoutLimits.Default;

        var surface = new TiledSurface(
            tree, tree.RootNode, 60, 60, limits,
            TreemapLayout.Compute(tree, tree.RootNode, 60, 60, limits));

        Assert.DoesNotContain(tree.RootNode, surface.Labels.Select(l => l.Node));
    }

    /// <summary>
    /// A directory of several hundred near-equal children defeats the size threshold — every
    /// rectangle is then big enough to label and none of them is interesting. Past a few dozen the
    /// labels are noise over the picture, and the list view is the honest way to read that many
    /// names.
    /// </summary>
    [Fact]
    public void TheLabelsAreCappedHoweverManyShapesWouldTakeOne()
    {
        Assert.Equal(64, Treemap(FlatTree(100)).Labels.Count);
    }

    [Fact]
    public void ALabelTakesTheColourThatContrastsWithTheShapeUnderIt()
    {
        var label = Treemap(FlatTree(4)).Labels[0];

        Assert.Contains(label.Colour, new[] { new TileColour(0, 0, 0), new TileColour(255, 255, 255) });
    }

    /// <summary>Rectangles read left to right, so their labels are neither turned nor centred.</summary>
    [Fact]
    public void ARectanglesLabelIsLeftAlignedAndUpright()
    {
        Assert.All(Treemap(FlatTree(6)).Labels, label =>
        {
            Assert.Equal(0, label.Rotation);
            Assert.False(label.Centred);
        });
    }

    /// <summary>
    /// A sunburst's labels lie along their own ring rather than across it, and none of them is ever
    /// upside down: past a quarter turn the label is turned back the other way, so the left half of
    /// the picture reads bottom-to-top rather than inverted.
    /// </summary>
    [Fact]
    public void ASunburstsLabelsLieAlongTheRingAndAreNeverUpsideDown()
    {
        var labels = Sunburst(FlatTree(6)).Labels;

        Assert.NotEmpty(labels);

        Assert.All(labels, label =>
        {
            Assert.InRange(label.Rotation, -90, 90);
            Assert.True(label.Centred);
        });
    }

    /// <summary>
    /// A wedge is measured by the straight line the text sits on. Six equal children of one ring
    /// are wide enough for that and sixty are not, and nothing in between is drawn with text
    /// running off both ends of it.
    /// </summary>
    [Fact]
    public void OnlyASectorWideEnoughToHoldTextIsLabelled()
    {
        Assert.NotEmpty(Sunburst(FlatTree(6)).Labels);
        Assert.Empty(Sunburst(FlatTree(60)).Labels);
    }

    /// <summary>
    /// The block standing in for what was too small to draw has no name to write in it, and giving
    /// it one of its siblings' would say the wrong thing about what is there.
    /// </summary>
    [Fact]
    public void TheBlockStandingInForOmittedSiblingsIsNotLabelled()
    {
        var tree = LopsidedTree();

        Assert.Contains(
            TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default),
            t => t.IsAggregate);

        Assert.DoesNotContain(ExploreTile.Aggregated, Treemap(tree).Labels.Select(l => l.Node));
        Assert.DoesNotContain(ExploreSector.Aggregated, Sunburst(tree).Labels.Select(l => l.Node));
    }

    private static IReadOnlyList<string> Names(ExploreTree tree, ExploreSurface surface) =>
        [.. surface.Labels.Select(l => tree.NameOf(l.Node))];

    private static ExploreSurface Treemap(ExploreTree tree) => new TiledSurface(
        tree,
        tree.RootNode,
        Width,
        Height,
        LayoutLimits.Default,
        TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default));

    private static ExploreSurface Sunburst(ExploreTree tree) =>
        new SunburstSurface(tree, tree.RootNode, Width, Height, LayoutLimits.Default);

    /// <summary>Equal children of one root, which is the shape that defeats a size threshold.</summary>
    private static ExploreTree FlatTree(int children)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, children).Select(i =>
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build();
    }

    /// <summary>One child too big to miss and five hundred too small to draw, which is what forces
    /// the block standing in for the rest.</summary>
    private static ExploreTree LopsidedTree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("big", IsDirectory: false, IsLink: false, Size: 1_000_000),
                .. Enumerable.Range(0, 500).Select(i =>
                    new ExploreChild($"small{i}", IsDirectory: false, IsLink: false, Size: 1)),
            ]);

        return builder.Build();
    }

    /// <summary>One directory with files in it, so the directory has something drawn inside it.</summary>
    private static ExploreTree NestedTree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var branch = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("branch", IsDirectory: true, IsLink: false, Size: 0)]);

        builder.AddChildren(
            branch,
            [.. Enumerable.Range(0, 4).Select(i =>
                new ExploreChild($"leaf{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build();
    }
}
