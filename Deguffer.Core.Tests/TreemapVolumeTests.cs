using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The two blocks a treemap draws beside the whole of a volume: what it has left, and what it has in
/// use that the scan did not count. Each in proportion to what was scanned, never inside it, and
/// never something a click can pick (§7.1).
/// </summary>
public sealed class TreemapVolumeTests
{
    private const int Width = 800;
    private const int Height = 600;

    /// <summary>
    /// The scan counts 3,000 bytes of a 12,000-byte volume with 6,000 free, so a quarter is in use
    /// and uncounted, a quarter is the scan and half is free.
    /// </summary>
    [Fact]
    public void EachPartTakesItsShareOfTheCanvasBesideTheRoot()
    {
        var tree = Folder();
        var tiles = TreemapLayout.Compute(
            tree, tree.RootNode, Width, Height, LayoutLimits.Default, new VolumeSpace(12_000, 6_000));

        var free = tiles.Single(tile => tile.IsFreeSpace);
        var unaccounted = tiles.Single(tile => tile.IsUnaccounted);
        var root = tiles.Single(tile => tile.Node == tree.RootNode);
        const double Canvas = (double)Width * Height;

        Assert.Equal(6_000, free.Bytes);
        Assert.Equal(3_000, unaccounted.Bytes);
        Assert.Equal(0.50, Area(free) / Canvas, 3);
        Assert.Equal(0.25, Area(unaccounted) / Canvas, 3);
        Assert.Equal(0.25, Area(root) / Canvas, 3);

        Assert.True(Apart(free, root), "the free space overlaps what was scanned");
        Assert.True(Apart(unaccounted, root), "the unaccounted use overlaps what was scanned");
        Assert.True(Apart(free, unaccounted), "the two blocks overlap");
    }

    /// <summary>
    /// The block this exists for. Without it, free space takes the share of the picture that the
    /// uncounted use belongs to, and a lower bound reads as the whole drive (§7.1).
    /// </summary>
    [Fact]
    public void FreeSpaceIsInProportionToTheWholeVolumeRatherThanToTheScan()
    {
        var tree = Folder();
        var tiles = TreemapLayout.Compute(
            tree, tree.RootNode, Width, Height, LayoutLimits.Default, new VolumeSpace(12_000, 6_000));

        // Against the scan alone, 6,000 free beside 3,000 counted would be two thirds of the canvas.
        Assert.Equal(0.5, Area(tiles.Single(tile => tile.IsFreeSpace)) / ((double)Width * Height), 3);
    }

    [Fact]
    public void AScanThatCountedEverythingInUseHasNoUnaccountedBlock()
    {
        var tree = Folder();
        var tiles = TreemapLayout.Compute(
            tree, tree.RootNode, Width, Height, LayoutLimits.Default, new VolumeSpace(9_000, 6_000));

        Assert.DoesNotContain(tiles, tile => tile.IsUnaccounted);
        Assert.Contains(tiles, tile => tile.IsFreeSpace);
    }

    /// <summary>
    /// A scan adds up the length of every file, and a compressed or sparse file occupies less than
    /// its length, so a scan can count more than the volume says is in use. That is no use to draw.
    /// </summary>
    [Fact]
    public void AScanThatCountedMoreThanIsInUseLeavesNothingUnaccounted()
    {
        Assert.Equal(0, new VolumeSpace(10_000, 6_000).UnaccountedBytes(5_000));
        Assert.Equal(1_000, new VolumeSpace(10_000, 6_000).UnaccountedBytes(3_000));
    }

    [Fact]
    public void NoBlockIsDrawnBesideNoVolume()
    {
        var tree = Folder();
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default);

        Assert.DoesNotContain(tiles, tile => !tile.IsNode);
        Assert.Equal((float)Width, tiles.Single(tile => tile.Node == tree.RootNode).Width);
    }

    /// <summary>
    /// A share too thin to point at is left out, and the root keeps the whole canvas rather than
    /// giving up a strip to a block nobody can see.
    /// </summary>
    [Fact]
    public void AShareThinnerThanTheSmallestTileIsLeftOut()
    {
        var tree = Folder();
        var tiles = TreemapLayout.Compute(
            tree, tree.RootNode, Width, Height, LayoutLimits.Default, new VolumeSpace(3_001, 1));

        Assert.DoesNotContain(tiles, tile => !tile.IsNode);
        Assert.Equal((float)Width, tiles.Single(tile => tile.Node == tree.RootNode).Width);
    }

    /// <summary>
    /// Free space is in proportion to a whole volume. A folder the reader has opened is drawn without
    /// either block, because beside one folder they would shrink the folder asked about to a corner.
    /// </summary>
    [Fact]
    public void AnOpenedFolderIsDrawnWithoutTheBlocks()
    {
        var tree = Folder();
        var folder = tree.ChildrenOf(tree.RootNode)[0];

        // Shares large enough to draw beside the folder, so the rule is what keeps them off.
        var surface = Treemap(tree, folder, ExploreView.Treemap, new VolumeSpace(12_000, 6_000));

        Assert.DoesNotContain(Hits(surface), hit => !hit.IsNode);
    }

    /// <summary>The blocks belong to the treemap. The icicle and the sunburst have no place for them.</summary>
    [Theory]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void OnlyTheTreemapDrawsTheBlocks(ExploreView view)
    {
        var tree = Folder();

        Assert.DoesNotContain(
            Hits(Treemap(tree, tree.RootNode, view, new VolumeSpace(12_000, 6_000))),
            hit => hit.IsFreeSpace || hit.IsUnaccounted);
    }

    /// <summary>
    /// A drawing says whether anything is beside its root, which is what decides whether the root can
    /// be opened out of it. Only a treemap of the root draws either block, and only where the block
    /// has room.
    /// </summary>
    [Fact]
    public void ADrawingSaysWhetherAnythingIsBesideTheRoot()
    {
        var tree = Folder();
        var volume = new VolumeSpace(12_000, 6_000);

        Assert.True(Treemap(tree, tree.RootNode, ExploreView.Treemap, volume).HasVolumeBeside);
        Assert.False(Treemap(tree, tree.RootNode, ExploreView.Treemap, new VolumeSpace(3_001, 1)).HasVolumeBeside);
        Assert.False(Treemap(tree, tree.RootNode, ExploreView.Treemap, VolumeSpace.None).HasVolumeBeside);
        Assert.False(Treemap(tree, tree.ChildrenOf(tree.RootNode)[0], ExploreView.Treemap, volume).HasVolumeBeside);
        Assert.False(Treemap(tree, tree.RootNode, ExploreView.Icicle, volume).HasVolumeBeside);
        Assert.False(Treemap(tree, tree.RootNode, ExploreView.Sunburst, volume).HasVolumeBeside);
    }

    /// <summary>
    /// Said of the whole picture, not of the canvas: a zoom into the root that leaves both blocks off
    /// the screen is still of a root drawn beside its volume, and still opens out of it.
    /// </summary>
    [Fact]
    public void AZoomThatLeavesTheBlocksOffTheCanvasStillSaysTheyAreBesideTheRoot()
    {
        var tree = Folder();
        var volume = new VolumeSpace(12_000, 6_000);
        var root = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, volume)
            .Single(tile => tile.Node == tree.RootNode);
        var viewport = MapViewport.Anchored(
            16, (root.X + (root.Width / 2)) / Width, (root.Y + (root.Height / 2)) / Height, 0.5, 0.5);

        var zoomed = ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Treemap, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, volume, viewport);

        Assert.DoesNotContain(Hits(zoomed), hit => hit.IsFreeSpace || hit.IsUnaccounted);
        Assert.True(zoomed.HasVolumeBeside);
    }

    /// <summary>
    /// §7.1: the map acts on what the user picked, and neither block is on the disk to be picked.
    /// The pointer finds each and says how much it is, and neither is a node.
    /// </summary>
    [Fact]
    public void PointingAtEitherBlockFindsNoNode()
    {
        var tree = Folder();
        var surface = Treemap(tree, tree.RootNode, ExploreView.Treemap, new VolumeSpace(12_000, 6_000));
        var hits = Hits(surface);

        var free = Assert.Single(hits.Where(hit => hit.IsFreeSpace).Distinct());
        var unaccounted = Assert.Single(hits.Where(hit => hit.IsUnaccounted).Distinct());

        Assert.False(free.IsNode);
        Assert.False(unaccounted.IsNode);
        Assert.Equal(6_000, free.Bytes);
        Assert.Equal(3_000, unaccounted.Bytes);
    }

    [Fact]
    public void NeitherBlockIsEverOutlined()
    {
        var tree = Folder();
        var surface = Treemap(tree, tree.RootNode, ExploreView.Treemap, new VolumeSpace(12_000, 6_000));

        Assert.Empty(surface.Outlines(new HashSet<int> { ExploreTile.FreeSpace, ExploreTile.Unaccounted }));
    }

    /// <summary>
    /// Each block is named with its own figure, which a caption cannot look up in the tree, and is
    /// painted flat in its own neutral.
    /// </summary>
    [Fact]
    public void EachBlockIsLabelledWithItsBytesAndPaintedFlat()
    {
        var tree = Folder();
        var surface = Treemap(tree, tree.RootNode, ExploreView.Treemap, new VolumeSpace(12_000, 6_000));

        Assert.Contains(surface.Labels, label => label.Node == ExploreTile.FreeSpace && label.Bytes == 6_000);
        Assert.Contains(surface.Labels, label => label.Node == ExploreTile.Unaccounted && label.Bytes == 3_000);

        var pixels = new byte[PixelBuffer.LengthFor(Width, Height)];
        surface.Paint(pixels, new TileColour(0, 0, 0));

        var lit = CushionShading.LightAt(0, 0);
        var tiles = TreemapLayout.Compute(
            tree, tree.RootNode, Width, Height, LayoutLimits.Default, new VolumeSpace(12_000, 6_000));

        foreach (var (block, colour) in new[]
        {
            (tiles.Single(tile => tile.IsFreeSpace), TilePalette.FreeSpace),
            (tiles.Single(tile => tile.IsUnaccounted), TilePalette.Unaccounted),
        })
        {
            var corner = At(pixels, (int)block.X + 2, (int)block.Y + 2);
            var across = At(pixels, (int)(block.X + block.Width) - 3, (int)(block.Y + block.Height) - 3);

            Assert.Equal(corner, across);
            Assert.Equal((byte)(colour.Red * lit), corner.Red);
            Assert.Equal(corner.Red, corner.Blue);
        }

        Assert.NotEqual(TilePalette.FreeSpace, TilePalette.Unaccounted);
        Assert.NotEqual(TilePalette.Aggregate, TilePalette.Unaccounted);
    }

    [Fact]
    public void TheWholeOfAVolumeIsGivenItsSpace()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 900, freeBytes: 500);

        Assert.Equal(new VolumeSpace(900, 500), VolumeSpace.Of(volumes, @"D:\"));
    }

    /// <summary>§6.3: a root in the extended-length form is the same volume.</summary>
    [Fact]
    public void AVolumeNamedInTheExtendedLengthFormIsStillTheWholeOfIt()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 900, freeBytes: 500);

        Assert.Equal(new VolumeSpace(900, 500), VolumeSpace.Of(volumes, @"\\?\D:\"));
    }

    [Fact]
    public void AFolderOnAVolumeIsGivenNoSpace()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 900, freeBytes: 500);

        Assert.Equal(VolumeSpace.None, VolumeSpace.Of(volumes, @"D:\work"));
    }

    /// <summary>
    /// A volume mounted at a folder is the whole of itself there, and the folder picker hands the
    /// path back without the separator the inventory reports it with.
    /// </summary>
    [Fact]
    public void AVolumeMountedAtAFolderIsTheWholeOfItselfThere()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\", totalBytes: 200, freeBytes: 100)
            .With(@"C:\Mount\", totalBytes: 800, freeBytes: 700);

        Assert.Equal(new VolumeSpace(800, 700), VolumeSpace.Of(volumes, @"C:\Mount"));
    }

    [Fact]
    public void AnyOfAVolumesMountPointsIsTheWholeOfIt()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\", totalBytes: 200, freeBytes: 100)
            .With(@"E:\", alsoMountedAt: [@"C:\Data\"], totalBytes: 1_000, freeBytes: 900);

        Assert.Equal(new VolumeSpace(1_000, 900), VolumeSpace.Of(volumes, @"C:\Data"));
    }

    [Theory]
    [InlineData(null, 500L)]
    [InlineData(900L, null)]
    public void AVolumeThatWouldNotStateBothFiguresIsGivenNoSpace(long? total, long? free)
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: total, freeBytes: free);

        Assert.Equal(VolumeSpace.None, VolumeSpace.Of(volumes, @"D:\"));
    }

    [Theory]
    [InlineData(@"\\server.test\share")]
    [InlineData(@"D:")]
    [InlineData(@"relative\path")]
    public void ALocationThatIsNotTheTopOfAKnownVolumeIsGivenNoSpace(string root)
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 900, freeBytes: 500);

        Assert.Equal(VolumeSpace.None, VolumeSpace.Of(volumes, root));
    }

    private static ExploreSurface Treemap(ExploreTree tree, int root, ExploreView view, VolumeSpace volume) =>
        ExploreSurface.Create(
            tree, root, view, Width, Height, scale: 1, textScale: 1, ShapeColours.ByBranch(ExploreScheme.Standard),
            ExploreSpacing.Comfortable, volume);

    private static double Area(ExploreTile tile) => (double)tile.Width * tile.Height;

    private static bool Apart(ExploreTile a, ExploreTile b) =>
        a.X >= b.X + b.Width || b.X >= a.X + a.Width || a.Y >= b.Y + b.Height || b.Y >= a.Y + a.Height;

    /// <summary>What the pointer finds on a grid across the whole canvas.</summary>
    private static List<ExploreHit> Hits(ExploreSurface surface)
    {
        var hits = new List<ExploreHit>();

        for (var y = 1; y < Height; y += 10)
        {
            for (var x = 1; x < Width; x += 10)
            {
                if (surface.At(x, y) is { } hit)
                {
                    hits.Add(hit);
                }
            }
        }

        return hits;
    }

    private static TileColour At(byte[] pixels, int x, int y)
    {
        var offset = ((y * Width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    /// <summary>A folder of three 1,000-byte files under the root, so there is a folder to open.</summary>
    private static ExploreTree Folder()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var folder = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("folder", IsDirectory: true, IsLink: false, Size: 0)]);

        builder.AddChildren(
            folder,
            [.. Enumerable.Range(0, 3).Select(i =>
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
