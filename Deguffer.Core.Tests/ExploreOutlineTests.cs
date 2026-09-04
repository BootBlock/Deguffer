using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where a drawing says each of its shapes is, so the shell can mark out what the user picked.
///
/// <para>§7.1 lets Explore act only on what was picked out by hand, and the four things it may do
/// include deleting. The outline is what says which shape that is, so it has to be the shape that
/// was actually drawn, and it has to be absent for anything that cannot be picked at all.</para>
/// </summary>
public sealed class ExploreOutlineTests
{
    private const int Width = 800;
    private const int Height = 800;

    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A rectangle's outline is that rectangle. The shell draws a line through these points and
    /// nothing corrects it afterwards, so a corner out of place is a line round the wrong shape.
    /// </summary>
    [Fact]
    public void ARectangleIsOutlinedWhereItWasLaidOut()
    {
        var tree = FlatTree(4);
        var surface = Treemap(tree);
        var node = tree.ChildrenOf(tree.RootNode)[0];

        var tile = TreemapLayout
            .Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default)
            .Single(t => t.Node == node);

        var outline = Assert.Single(surface.Outlines(new HashSet<int> { node }));

        Assert.Equal(node, outline.Node);
        Assert.Equal(
            [
                new ExplorePoint(tile.X, tile.Y),
                new ExplorePoint(tile.X + tile.Width, tile.Y),
                new ExplorePoint(tile.X + tile.Width, tile.Y + tile.Height),
                new ExplorePoint(tile.X, tile.Y + tile.Height),
            ],
            outline.Points);
    }

    /// <summary>
    /// One pass over the shapes, whichever view, and every asked-for node that was drawn comes back
    /// exactly once. A node drawn twice would be outlined twice, and one missed is a selection the
    /// user cannot see.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void EveryPickedNodeThatWasDrawnIsOutlinedOnce(ExploreView view)
    {
        var tree = FlatTree(6);
        var surface = Surface(tree, view);
        var picked = new HashSet<int>(tree.ChildrenOf(tree.RootNode)[..3].ToArray());

        var outlines = surface.Outlines(picked);

        Assert.Equal(picked.Order(), outlines.Select(o => o.Node).Order());
        Assert.All(outlines, outline => Assert.True(outline.Points.Count >= 3, "an outline needs an area"));
    }

    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void NothingIsOutlinedForAnEmptySelection(ExploreView view) =>
        Assert.Empty(Surface(FlatTree(6), view).Outlines(new HashSet<int>()));

    /// <summary>
    /// A node this drawing did not draw has no outline, rather than an empty one or a throw. The
    /// selection outlives a descend and a mid-scan snapshot, so being asked about something that is
    /// not on screen is ordinary rather than a mistake.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void ANodeThisDrawingDidNotDrawIsSimplyAbsent(ExploreView view)
    {
        var tree = FlatTree(6);

        Assert.Empty(Surface(tree, view).Outlines(new HashSet<int> { tree.NodeCount + 100 }));
    }

    /// <summary>
    /// The block standing in for items too small to draw is never outlined. §7.1 has no bulk action,
    /// so it cannot be picked, and a highlight on it would invite a click that does nothing.
    ///
    /// <para>Each of these two proves its own fixture first. A tree the layout does not actually
    /// aggregate makes the assertion below vacuously true, which is how the treemap half of this
    /// went green against a version that outlined aggregates.</para>
    /// </summary>
    [Fact]
    public void TheBlockStandingInForSmallItemsIsNeverOutlinedOnATreemap()
    {
        var tree = LongTailTree();

        Assert.Contains(
            TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default),
            tile => tile.IsAggregate);

        Assert.Empty(Treemap(tree).Outlines(new HashSet<int> { ExploreTile.Aggregated }));
    }

    [Fact]
    public void TheBlockStandingInForSmallItemsIsNeverOutlinedOnASunburst()
    {
        var tree = LongTailTree();

        Assert.Contains(
            SunburstLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default).Sectors,
            sector => sector.IsAggregate);

        Assert.Empty(Sunburst(tree).Outlines(new HashSet<int> { ExploreSector.Aggregated }));
    }

    /// <summary>
    /// A sunburst wedge is outlined round its own ring: every point sits on one of the two radii it
    /// was laid out between, and the outline spans the sweep rather than cutting the corner.
    /// </summary>
    [Fact]
    public void AWedgeIsOutlinedAlongItsOwnRing()
    {
        var tree = FlatTree(6);
        var surface = Sunburst(tree);
        var node = tree.ChildrenOf(tree.RootNode)[0];

        var sector = SunburstLayout
            .Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default)
            .Sectors.Single(s => s.Node == node);

        var outline = Assert.Single(surface.Outlines(new HashSet<int> { node }));

        var radii = outline.Points
            .Select(p => MathF.Sqrt(((p.X - (Width / 2f)) * (p.X - (Width / 2f)))
                                    + ((p.Y - (Height / 2f)) * (p.Y - (Height / 2f)))))
            .ToList();

        Assert.All(radii, radius => Assert.InRange(radius, sector.InnerRadius - 0.5f, sector.OuterRadius + 0.5f));

        // Both edges are traced, not just the far one: a wedge outlined only along its outer arc
        // would be an arc rather than a shape.
        Assert.Contains(radii, r => MathF.Abs(r - sector.InnerRadius) < 0.5f);
        Assert.Contains(radii, r => MathF.Abs(r - sector.OuterRadius) < 0.5f);
    }

    /// <summary>
    /// The disc in the middle closes on itself, so it is outlined once round its rim. Walking back
    /// along an inner edge it does not have would draw a spike to the centre across the picture.
    /// </summary>
    [Fact]
    public void TheDiscInTheMiddleIsOutlinedWithNoSpikeToTheCentre()
    {
        var tree = FlatTree(6);
        var surface = Sunburst(tree);

        var sunburst = SunburstLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default);
        var disc = sunburst.Sectors.Single(s => s.Depth == 0);

        var outline = Assert.Single(surface.Outlines(new HashSet<int> { disc.Node }));

        Assert.All(
            outline.Points,
            point => Assert.InRange(
                MathF.Sqrt(((point.X - sunburst.CentreX) * (point.X - sunburst.CentreX))
                           + ((point.Y - sunburst.CentreY) * (point.Y - sunburst.CentreY))),
                disc.OuterRadius - 0.5f,
                disc.OuterRadius + 0.5f));
    }

    private static ExploreSurface Surface(ExploreTree tree, ExploreView view) =>
        ExploreSurface.Create(
            tree, tree.RootNode, view, Width, Height, scale: 1, ExploreColouring.Branch, Now);

    private static ExploreSurface Treemap(ExploreTree tree) => Surface(tree, ExploreView.Treemap);

    private static ExploreSurface Sunburst(ExploreTree tree) => Surface(tree, ExploreView.Sunburst);

    private static ExploreTree FlatTree(int children)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, children).Select(i =>
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// A few large children and a long tail of tiny ones, which is the shape that makes a layout
    /// aggregate. Equal children do not: however many there are, they either all fit or the canvas
    /// is too small for any of them.
    /// </summary>
    private static ExploreTree LongTailTree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                .. Enumerable.Range(0, 4).Select(i =>
                    new ExploreChild($"big{i}", IsDirectory: false, IsLink: false, Size: 1_000_000)),
                .. Enumerable.Range(0, 500).Select(i =>
                    new ExploreChild($"tiny{i}", IsDirectory: false, IsLink: false, Size: 1)),
            ]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
