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

    /// <summary>
    /// The colouring these tests are not about. They ask where the shapes and the labels go, which
    /// no colouring changes, so they all pin the shipped one rather than each stating a choice.
    /// <see cref="AgeColouringTests"/> is where the other one is exercised.
    /// </summary>
    private const ExploreColouring Branch = ExploreColouring.Branch;

    /// <summary>Any instant will do while the colouring is by branch, and it must not be the clock.</summary>
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.List)]
    [InlineData(ExploreView.Tree)]
    public void EveryViewDrawnInRectanglesAsksForTheSameSurface(ExploreView view)
    {
        var tree = FlatTree(10);

        Assert.IsType<TiledSurface>(
            ExploreSurface.Create(
                tree, tree.RootNode, view, Width, Height, scale: 1, textScale: 1, Branch, ExploreScheme.Standard, Now, ExploreSpacing.Comfortable, VolumeSpace.None));
    }

    [Fact]
    public void TheSunburstAsksForItsOwn()
    {
        var tree = FlatTree(10);

        Assert.IsType<SunburstSurface>(
            ExploreSurface.Create(
                tree, tree.RootNode, ExploreView.Sunburst, Width, Height, scale: 1, textScale: 1, Branch, ExploreScheme.Standard, Now, ExploreSpacing.Comfortable, VolumeSpace.None));
    }

    /// <summary>
    /// A scan still running publishes a tree ordered by name, and neither the treemap nor the
    /// sunburst can be drawn from one — both refuse it rather than draw a picture that lies. So the
    /// icicle stands in until the scan finishes, which is also what stops a repaint throwing.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Sunburst)]
    [InlineData(ExploreView.Icicle)]
    public void AScanStillRunningIsDrawnAsAnIcicleWhicheverViewWasPicked(ExploreView view)
    {
        var tree = NamedTree(10);

        var surface = ExploreSurface.Create(
            tree, tree.RootNode, view, Width, Height, scale: 1, textScale: 1, Branch, ExploreScheme.Standard, Now, ExploreSpacing.Comfortable, VolumeSpace.None);

        Assert.IsType<TiledSurface>(surface);
        Assert.NotEmpty(surface.Labels);
    }

    /// <summary>
    /// A folder with a band is named in it, above everything drawn inside it, so its name and its
    /// first child's cannot overprint.
    /// </summary>
    [Fact]
    public void AParentIsNamedInItsBandAboveItsChildren()
    {
        var tree = NestedTree();
        var surface = Treemap(tree);
        var branch = surface.Labels.Single(label => tree.NameOf(label.Node) == "branch");
        var leaf = surface.Labels.Single(label => tree.NameOf(label.Node) == "leaf0");

        Assert.True(
            branch.Y + LayoutLimits.Default.MinimumLabelHeight <= leaf.Y,
            $"the folder's name at {branch.Y} runs into its child's at {leaf.Y}");
    }

    /// <summary>
    /// Where a folder has children drawn inside it and no band, it gets no label. Its children are
    /// inset by a pixel or two, so its label and its first child's would land on top of each other
    /// and overprint into an unreadable stack.
    /// </summary>
    [Fact]
    public void AParentWithNoBandAndSomethingDrawnInsideItIsNotLabelled()
    {
        // Too short for a band: two of them do not fit.
        var tree = NestedTree();
        var limits = LayoutLimits.Default;
        var height = (int)(limits.HeaderHeight * 2);

        var surface = new TiledSurface(
            tree, tree.RootNode, (int)Width, height, limits, ShapeColours.ByBranch(ExploreScheme.Standard),
            TreemapLayout.Compute(tree, tree.RootNode, Width, height, limits));

        Assert.DoesNotContain("branch", Names(tree, surface));
    }

    /// <summary>
    /// The node being drawn is named only where it has a band. Anywhere else a label for it would
    /// sit in the top-left corner over its own first child, and the breadcrumb names it anyway.
    /// </summary>
    [Fact]
    public void TheNodeBeingDrawnIsNotLabelledWithoutABand()
    {
        // Small enough that nothing inside the root is big enough to draw, and too short for a band.
        // The covering rule then has nothing to say about the root, so this rule is the only thing
        // keeping its label off.
        var tree = FlatTree(500);
        var limits = LayoutLimits.Default;

        var surface = new TiledSurface(
            tree, tree.RootNode, 60, 30, limits, ShapeColours.ByBranch(ExploreScheme.Standard),
            TreemapLayout.Compute(tree, tree.RootNode, 60, 30, limits));

        Assert.DoesNotContain(tree.RootNode, surface.Labels.Select(l => l.Node));
    }

    /// <summary>
    /// A directory of several hundred near-equal children defeats the size threshold — every
    /// rectangle is then big enough to label and none of them is interesting. Past a few dozen the
    /// labels are noise over the picture, and the list view is the honest way to read that many
    /// names. The folder's own name in its band is not one of them.
    /// </summary>
    [Fact]
    public void TheLabelsInsideShapesAreCappedHoweverManyShapesWouldTakeOne()
    {
        var tree = FlatTree(100);

        Assert.Equal(64, Treemap(tree).Labels.Count(label => label.Node != tree.RootNode));
    }

    /// <summary>
    /// The largest shapes are named first. The layout emits shapes depth first, so taking them in
    /// that order spent the whole allowance on whatever came first and left larger shapes unnamed.
    /// </summary>
    [Fact]
    public void TheLargestShapesAreTheOnesLabelled()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, 200).Select(i =>
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 20_000 - (i * 90)))]);

        var tree = builder.Build(ExploreChildOrder.BySize);
        var limits = LayoutLimits.Default;
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits);
        var labelled = new TiledSurface(tree, tree.RootNode, Width, Height, limits, ShapeColours.ByBranch(ExploreScheme.Standard), tiles)
            .Labels.Select(label => label.Node).ToHashSet();

        var candidates = tiles.Where(tile => tile.Depth == 1 && tile.HasRoomForALabel(limits)).ToList();
        var smallestNamed = candidates.Where(tile => labelled.Contains(tile.Node)).Min(tile => tile.Width * tile.Height);
        var unnamed = candidates.Where(tile => !labelled.Contains(tile.Node)).ToList();

        // More shapes with room for a label than the allowance, or this proves nothing.
        Assert.NotEmpty(unnamed);
        Assert.All(unnamed, tile => Assert.True(
            tile.Width * tile.Height <= smallestNamed,
            $"a shape of {tile.Width * tile.Height} went unnamed while one of {smallestNamed} was named"));
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
    ///
    /// <para>The threshold is raised so that every child is gathered into one block covering most of
    /// the canvas. A block that is merely small is left unlabelled by the size rule, whatever this
    /// one does, so the fixture has to make it big enough to be a candidate.</para>
    /// </summary>
    [Fact]
    public void TheBlockStandingInForOmittedSiblingsIsNotLabelled()
    {
        var tree = FlatTree(200);
        var limits = LayoutLimits.Default with { MinimumTileSize = 100 };

        var tiles = Treemap(tree, limits).Labels;
        var sectors = Sunburst(tree, limits).Labels;

        Assert.DoesNotContain(ExploreTile.Aggregated, tiles.Select(l => l.Node));
        Assert.DoesNotContain(ExploreSector.Aggregated, sectors.Select(l => l.Node));
    }

    private static IReadOnlyList<string> Names(ExploreTree tree, ExploreSurface surface) =>
        [.. surface.Labels.Select(l => tree.NameOf(l.Node))];

    private static ExploreSurface Treemap(ExploreTree tree) => Treemap(tree, LayoutLimits.Default);

    private static ExploreSurface Treemap(ExploreTree tree, LayoutLimits limits) => new TiledSurface(
        tree,
        tree.RootNode,
        Width,
        Height,
        limits,
        ShapeColours.ByBranch(ExploreScheme.Standard),
        TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits));

    private static ExploreSurface Sunburst(ExploreTree tree) => Sunburst(tree, LayoutLimits.Default);

    private static ExploreSurface Sunburst(ExploreTree tree, LayoutLimits limits) =>
        new SunburstSurface(tree, tree.RootNode, Width, Height, limits, ShapeColours.ByBranch(ExploreScheme.Standard));

    /// <summary>Equal children of one root, which is the shape that defeats a size threshold.</summary>
    private static ExploreTree FlatTree(int children)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, children).Select(i =>
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>The same shape in the order a scan still running publishes it.</summary>
    private static ExploreTree NamedTree(int children)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, children).Select(i =>
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build(ExploreChildOrder.ByName);
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

        return builder.Build(ExploreChildOrder.BySize);
    }
}
