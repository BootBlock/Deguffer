using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Memory;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The memory tree is drawn by the code that draws a scanned drive: the same treemap, icicle and
/// sunburst, read through <see cref="ISizedTree"/>. These prove each one lays the memory tree out
/// by its sizes, which is the whole claim a picture of memory makes. Every name and figure is invented.
/// </summary>
public sealed class MemoryTreeLayoutTests
{
    private const float Width = 800;
    private const float Height = 600;

    /// <summary>
    /// Physical memory of 16,000 MB: 4,000 in two applications, 500 in a service host, a 3,000 system
    /// cache, a 200 pool, and the 8,300 nothing attributes. Every one of the three parts holds
    /// something, and each is big enough that none is aggregated away.
    /// </summary>
    private static readonly MemoryTree Tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
        .Process(100, 1, "alpha.exe", 3_000, created: 10)
        .Process(200, 100, "beta.exe", 1_000, created: 20)
        .Process(300, 1, "gamma.exe", 500, created: 30)
        .Service("ExampleIndexer", host: 300)
        .Build());

    [Fact]
    public void TheTreemapGivesEachPartAreaInProportionToItsSize()
    {
        var tiles = TreemapLayout.Compute(Tree, Tree.RootNode, Width, Height, LayoutLimits.Default);

        // The root keeps a frame round its children, a band along its top and a gap down its sides
        // and bottom, so they share what is inside it.
        var limits = LayoutLimits.Default;
        var available = (Width - (limits.ContainerGap * 2)) * (Height - limits.HeaderHeight - limits.ContainerGap);
        var total = (double)Tree.SizeOf(Tree.RootNode);
        var parts = tiles.Where(tile => tile.Depth == 1 && !tile.IsAggregate && Tree.SizeOf(tile.Node) > 0).ToArray();

        Assert.Equal(3, parts.Length);

        foreach (var tile in parts)
        {
            var expected = available * tile.Bytes / total;
            Assert.InRange(tile.Width * tile.Height, expected * 0.97, expected * 1.03);
        }
    }

    [Fact]
    public void TheIcicleGivesEachPartWidthInProportionToItsSize()
    {
        var tiles = IcicleLayout.Compute(Tree, Tree.RootNode, Width, Height, LayoutLimits.Default);
        var total = (double)Tree.SizeOf(Tree.RootNode);
        var parts = tiles.Where(tile => tile.Depth == 1 && !tile.IsAggregate).ToArray();

        Assert.Equal(3, parts.Length);

        foreach (var tile in parts)
        {
            Assert.InRange(tile.Width, (Width * tile.Bytes / total) - 0.5, (Width * tile.Bytes / total) + 0.5);
        }
    }

    [Fact]
    public void TheSunburstGivesEachPartAnAngleInProportionToItsSize()
    {
        var sunburst = SunburstLayout.Compute(Tree, Tree.RootNode, Width, Height, LayoutLimits.Default);
        var total = (double)Tree.SizeOf(Tree.RootNode);
        var ring = sunburst.Sectors.Where(sector => sector.Depth == 1 && !sector.IsAggregate).ToArray();

        Assert.NotEmpty(ring);

        foreach (var sector in ring)
        {
            Assert.InRange(sector.SweepAngle, (Math.Tau * sector.Bytes / total) - 0.001, (Math.Tau * sector.Bytes / total) + 0.001);
        }
    }

    /// <summary>
    /// A process with a child is drawn as a frame holding its own share and the child, which is only
    /// possible if the layout descends into it.
    /// </summary>
    [Fact]
    public void TheTreemapDescendsIntoAProcessWithChildren()
    {
        var tiles = TreemapLayout.Compute(Tree, Tree.RootNode, Width, Height, LayoutLimits.Default);

        Assert.Contains(tiles, tile => tile.Node == Tree.Find(new MemoryNodeKey(MemoryPart.Process, 100, 10)));
        Assert.Contains(tiles, tile => tile.Node == Tree.Find(new MemoryNodeKey(MemoryPart.OwnShare, 100, 10)));
        Assert.Contains(tiles, tile => tile.Node == Tree.Find(new MemoryNodeKey(MemoryPart.Process, 200, 20)));
    }

    /// <summary>
    /// Age bands belong to the tree whose dates they hold. A node number means nothing in any other
    /// tree, so a picture of memory banded by a drive's dates would read as ages of things that have
    /// none, which §7.2 forbids. Before the seam that was impossible; now it is refused.
    /// </summary>
    [Fact]
    public void ADrawingRefusesColoursThatHoldAnotherTreesDates()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("file", IsDirectory: false, IsLink: false, 1_000)]);

        var drive = builder.Build(ExploreChildOrder.BySize);
        var dates = ShapeColours.For(drive, ExploreColouring.Age, ExploreScheme.Standard, new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Throws<ArgumentException>(() =>
            ExploreSurface.Create(Tree, Tree.RootNode, ExploreView.Treemap, (int)Width, (int)Height, scale: 1, textScale: 1, dates, ExploreSpacing.Comfortable, VolumeSpace.None));
    }

    /// <summary>
    /// Each view is pointed at somewhere it draws: the middle for a treemap and for a sunburst's own
    /// disc, and the first row for an icicle, which fills the canvas downwards one level at a time and
    /// has nothing at the middle of a tree this shallow.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap, Width / 2, Height / 2)]
    [InlineData(ExploreView.Sunburst, Width / 2, Height / 2)]
    [InlineData(ExploreView.Icicle, Width / 2, 8)]
    public void EveryViewDrawsTheMemoryTreeAndFindsAPartUnderThePointer(ExploreView view, float x, float y)
    {
        var surface = ExploreSurface.Create(Tree, Tree.RootNode, view, (int)Width, (int)Height, scale: 1, textScale: 1, ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None);
        var pixels = new byte[PixelBuffer.LengthFor((int)Width, (int)Height)];

        surface.Paint(pixels, new TileColour(32, 32, 32));

        Assert.NotNull(surface.At(x, y));
        Assert.NotEmpty(surface.Labels);
    }
}
