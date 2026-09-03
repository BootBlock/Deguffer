using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sunburst's claim is the icicle's in polar coordinates: one ring per level, and angle exactly
/// proportional to size within a ring. It carries one more obligation the other layouts do not —
/// the sectors have to come out ordered by ring and then by angle, because that order is what lets
/// the hit test find a sector by one division and one binary search.
/// </summary>
public sealed class SunburstLayoutTests
{
    private const float Width = 800;
    private const float Height = 400;

    /// <summary>The floor under a ring's width, which is what <c>RowHeight</c> means to this layout.</summary>
    private const float MinimumRing = 20;

    /// <summary>The largest radius that fits, which is half of the shorter side of the canvas.</summary>
    private const float Radius = Height / 2;

    /// <summary>
    /// One ring for the node being drawn and one per level below it, which is what this canvas
    /// gets: it has room for ten rings of the minimum width, so the depth cap decides instead.
    /// </summary>
    private const int RingCount = 7;

    private const float RingWidth = Radius / RingCount;

    [Fact]
    public void EachLevelSitsOnItsOwnRing()
    {
        var sunburst = Layout(NestedTree(depth: 5), Sized(LayoutLimits.Default));

        Assert.Equal(RingWidth, sunburst.RingWidth, 3);

        foreach (var sector in sunburst.Sectors)
        {
            Assert.Equal(sector.Depth * RingWidth, sector.InnerRadius, 3);
            Assert.Equal(RingWidth, sector.OuterRadius - sector.InnerRadius, 3);
        }
    }

    /// <summary>
    /// The rings divide the radius rather than being a fixed width taken out of it. Fixing the
    /// width instead would make the picture deeper on a larger window and leave the first ring —
    /// the one carrying the largest wedges — the same few pixels across however much room there is.
    /// </summary>
    [Fact]
    public void TheRingsDivideTheRadiusAndFillIt()
    {
        var sunburst = Layout(NestedTree(depth: 20), Sized(LayoutLimits.Default));

        Assert.Equal(Radius, sunburst.Radius);
        Assert.Equal(Radius, sunburst.RingWidth * RingCount, 3);
        Assert.All(sunburst.Sectors, s => Assert.InRange(s.OuterRadius, 0, Radius));
    }

    /// <summary>
    /// Levels are capped the way the treemap's nesting is, and for the same reason: past the cap
    /// the wedges are slivers of slivers and the cost is paid on every repaint.
    /// </summary>
    [Fact]
    public void TheLevelsAreCappedTheWayTheTreemapsAre()
    {
        var limits = Sized(LayoutLimits.Default) with { MaximumDepth = 3 };
        var sunburst = Layout(NestedTree(depth: 20), limits);

        Assert.Equal(3, sunburst.Sectors.Max(s => s.Depth));
        Assert.Equal(Radius / 4, sunburst.RingWidth, 3);
    }

    /// <summary>
    /// A window with no room for the full depth shows fewer levels rather than unreadable ones, so
    /// the minimum ring width is a floor and not a fixed size.
    /// </summary>
    [Fact]
    public void ASmallCanvasShowsFewerLevelsRatherThanThinnerRings()
    {
        var tree = NestedTree(depth: 20);
        var sunburst = SunburstLayout.Compute(tree, tree.RootNode, 90, 90, Sized(LayoutLimits.Default));

        Assert.Equal(1, sunburst.Sectors.Max(s => s.Depth));
        Assert.Equal(22.5f, sunburst.RingWidth, 3);
    }

    /// <summary>The root is the disc in the middle rather than a ring, and it is the whole circle.</summary>
    [Fact]
    public void TheRootIsTheWholeDiscInTheMiddle()
    {
        var sunburst = Layout(TreeOf(500, 300, 200), Sized(LayoutLimits.Default));
        var root = sunburst.Sectors[0];

        Assert.Equal(0, root.Depth);
        Assert.Equal(0f, root.InnerRadius);
        Assert.Equal(0f, root.StartAngle);
        Assert.Equal(MathF.Tau, root.SweepAngle, 4);
    }

    [Fact]
    public void SweepIsProportionalToSizeWithinARing()
    {
        var sunburst = Layout(TreeOf(500, 300, 200), Sized(LayoutLimits.Default));

        foreach (var sector in sunburst.Sectors.Where(s => s.Depth == 1))
        {
            Assert.Equal(MathF.Tau * sector.Bytes / 1000f, sector.SweepAngle, 3);
        }
    }

    /// <summary>
    /// Boundaries come from a running total rather than from each child's own rounded sweep, so the
    /// last child of a full ring closes the circle instead of drifting off it.
    /// </summary>
    [Fact]
    public void TheChildrenFillTheirParentWithNoGapAndNoOverlap()
    {
        var limits = Sized(LayoutLimits.Default) with { MinimumTileSize = 0.01f };

        var ring = Layout(TreeOf([.. Enumerable.Range(1, 40).Select(i => (long)i)]), limits)
            .Sectors
            .Where(s => s.Depth == 1)
            .OrderBy(s => s.StartAngle)
            .ToList();

        Assert.Equal(40, ring.Count);
        Assert.Equal(0f, ring[0].StartAngle);

        for (var i = 1; i < ring.Count; i++)
        {
            Assert.Equal(ring[i - 1].StartAngle + ring[i - 1].SweepAngle, ring[i].StartAngle, 4);
        }

        Assert.Equal(MathF.Tau, ring[^1].StartAngle + ring[^1].SweepAngle, 3);
    }

    [Fact]
    public void WhatIsTooNarrowToDrawIsAggregatedRatherThanDropped()
    {
        var limits = Sized(LayoutLimits.Default) with { MinimumTileSize = 20 };
        var tree = TreeOf([1_000_000, .. Enumerable.Repeat(1L, 500)]);

        var aggregate = Assert.Single(Layout(tree, limits).Sectors, s => s.IsAggregate);

        Assert.Equal(500, aggregate.Bytes);
    }

    /// <summary>
    /// The residual wedge is not only for what was too narrow to draw. A directory can total more
    /// than its children do, and then every child is drawn and the ring still has a hole in it —
    /// which reads as space nothing is using rather than as bytes nobody itemised.
    /// </summary>
    [Fact]
    public void AParentItsChildrenDoNotAccountForStillClosesItsRing()
    {
        var tree = HeavyDirectoryTree(own: 600, child: 400);

        var ring = Layout(tree, Sized(LayoutLimits.Default)).Sectors.Where(s => s.Depth == 2).ToList();

        var aggregate = Assert.Single(ring, s => s.IsAggregate);

        Assert.Equal(600, aggregate.Bytes);
        Assert.Equal(MathF.Tau, ring.Sum(s => s.SweepAngle), 3);
    }

    /// <summary>
    /// A wedge is narrowest at its inner edge, so that is where it is measured. One wide enough to
    /// see at the outer edge and a fraction of a pixel across at the inner one cannot reliably be
    /// pointed at, which is as much of what <see cref="LayoutLimits.MinimumTileSize"/> means as
    /// drawing is.
    /// </summary>
    [Fact]
    public void ANarrowWedgeIsJudgedAtItsInnerEdgeNotItsOuter()
    {
        // One thousandth of the circle, in the first ring. That arc is 0.18 pixels at the ring's
        // inner edge and 0.36 at its outer one, so a threshold between the two is drawn by a rule
        // reading the outer edge and aggregated by the rule that reads the inner one.
        var tree = TreeOf([999, 1]);
        var limits = Sized(LayoutLimits.Default) with { MinimumTileSize = 0.25f };

        var sectors = Layout(tree, limits).Sectors;

        Assert.DoesNotContain(sectors, s => s.Bytes == 1 && !s.IsAggregate);
        Assert.Contains(sectors, s => s.IsAggregate && s.Bytes == 1);
    }

    /// <summary>
    /// The property the hit test is built on. Depth first, then angle, with no exception for the
    /// aggregate that closes a ring — which is why the layout queues that one rather than emitting
    /// it at the point where it decides to make it.
    /// </summary>
    [Fact]
    public void SectorsComeOutOrderedByRingAndThenByAngle()
    {
        var limits = Sized(LayoutLimits.Default) with { MinimumTileSize = 4 };
        var sectors = Layout(WideTree(), limits).Sectors;

        Assert.Contains(sectors, s => s.IsAggregate);

        for (var i = 1; i < sectors.Count; i++)
        {
            Assert.True(
                sectors[i].Depth > sectors[i - 1].Depth
                || (sectors[i].Depth == sectors[i - 1].Depth
                    && sectors[i].StartAngle >= sectors[i - 1].StartAngle),
                $"sector {i} at depth {sectors[i].Depth} angle {sectors[i].StartAngle} follows "
                    + $"depth {sectors[i - 1].Depth} angle {sectors[i - 1].StartAngle}");
        }
    }

    [Fact]
    public void ACanvasWithNoRoomForOneRingDrawsNothing()
    {
        var tree = TreeOf(100);

        Assert.Empty(
            SunburstLayout.Compute(
                tree, tree.RootNode, Width, (MinimumRing * 2) - 2, Sized(LayoutLimits.Default))
                .Sectors);
    }

    /// <summary>
    /// A name order is what a scan still running publishes, and the residual wedge cannot be built
    /// from one: it closes the ring from the first child too narrow to draw onwards, which is only
    /// the omitted children where the small ones are the tail. <see cref="TreemapLayout"/> refuses
    /// the same order for its own reason.
    /// </summary>
    [Fact]
    public void ATreeOrderedByNameIsRefusedRatherThanDrawn()
    {
        var tree = NamedTreeOf(500, 300, 200);

        Assert.Throws<ArgumentException>(() => Layout(tree, Sized(LayoutLimits.Default)));
    }

    private static Sunburst Layout(ExploreTree tree, LayoutLimits limits) =>
        SunburstLayout.Compute(tree, tree.RootNode, Width, Height, limits);

    /// <summary>
    /// The ring floor is a limit like the others, so the tests state it the same way rather than
    /// carrying a second number that has to be kept in step with the one the layout reads.
    /// </summary>
    private static LayoutLimits Sized(LayoutLimits limits) => limits with { RowHeight = MinimumRing };

    /// <summary>
    /// A directory carrying bytes of its own beside a child, so that its total is more than its
    /// children add up to.
    /// </summary>
    private static ExploreTree HeavyDirectoryTree(long own, long child)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var directory = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("dir", IsDirectory: true, IsLink: false, own)]);

        builder.AddChildren(
            directory,
            [new ExploreChild("file", IsDirectory: false, IsLink: false, child)]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>Children in the order a scan still running publishes them.</summary>
    private static ExploreTree NamedTreeOf(params long[] sizes)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. sizes.Select((size, i) => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, size))]);

        return builder.Build(ExploreChildOrder.ByName);
    }

    private static ExploreTree TreeOf(params long[] sizes)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. sizes.Select((size, i) => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, size))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>Two levels, the second wide enough that a ring runs out of room part way round.</summary>
    private static ExploreTree WideTree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var branches = new List<int>();

        for (var i = 0; i < 6; i++)
        {
            branches.Add(builder.AddChildren(ExploreTreeBuilder.RootNode, [
                new ExploreChild($"dir{i}", IsDirectory: true, IsLink: false, Size: 0),
            ]));
        }

        foreach (var branch in branches)
        {
            builder.AddChildren(
                branch,
                [.. Enumerable.Range(1, 30).Select(i =>
                    new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: i * 100L))]);
        }

        return builder.Build(ExploreChildOrder.BySize);
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
