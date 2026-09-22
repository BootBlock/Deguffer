using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The block a treemap draws beside the whole of a volume for what it has left: in proportion to
/// what was scanned, never inside it, and never something a click can pick (§7.1).
/// </summary>
public sealed class TreemapFreeSpaceTests
{
    private const int Width = 800;
    private const int Height = 600;

    [Fact]
    public void FreeSpaceTakesItsShareOfTheCanvasBesideTheRoot()
    {
        var tree = Folder();
        var used = tree.SizeOf(tree.RootNode);
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, used * 3);

        var free = tiles.Single(tile => tile.IsFreeSpace);
        var root = tiles.Single(tile => tile.Node == tree.RootNode);
        var canvas = (double)Width * Height;

        Assert.Equal(used * 3, free.Bytes);
        Assert.Equal(0.75, free.Width * free.Height / canvas, 3);
        Assert.Equal(0.25, root.Width * root.Height / canvas, 3);

        // Beside the root rather than over it or inside it.
        Assert.True(
            free.X >= root.X + root.Width || root.X >= free.X + free.Width
            || free.Y >= root.Y + root.Height || root.Y >= free.Y + free.Height,
            "the free space overlaps what was scanned");
    }

    [Fact]
    public void NoFreeSpaceIsDrawnWhereNoneWasGiven()
    {
        var tree = Folder();
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default);

        Assert.DoesNotContain(tiles, tile => tile.IsFreeSpace);
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
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, freeBytes: 1);

        Assert.DoesNotContain(tiles, tile => tile.IsFreeSpace);
        Assert.Equal((float)Width, tiles.Single(tile => tile.Node == tree.RootNode).Width);
    }

    /// <summary>
    /// Free space is in proportion to a whole volume. A folder the reader has opened is drawn without
    /// it, because beside one folder the block would shrink the folder asked about to a corner.
    /// </summary>
    [Fact]
    public void AnOpenedFolderIsDrawnWithoutFreeSpace()
    {
        var tree = Folder();
        var folder = tree.ChildrenOf(tree.RootNode)[0];

        // As much free space as the folder holds, so a block beside it would be half the canvas
        // rather than a share too thin to draw, which would pass this without the rule.
        var surface = Treemap(tree, folder, ExploreView.Treemap, freeBytes: tree.SizeOf(folder));

        Assert.DoesNotContain(Hits(surface), hit => hit.IsFreeSpace);
    }

    /// <summary>The block belongs to the treemap. The icicle and the sunburst have no place for it.</summary>
    [Theory]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void OnlyTheTreemapDrawsFreeSpace(ExploreView view)
    {
        var tree = Folder();

        var free = tree.SizeOf(tree.RootNode);

        Assert.DoesNotContain(Hits(Treemap(tree, tree.RootNode, view, free)), hit => hit.IsFreeSpace);
    }

    /// <summary>
    /// §7.1: the map acts on what the user picked, and free space is not on the disk to be picked.
    /// The pointer finds it and says how much it is, and it is not a node.
    /// </summary>
    [Fact]
    public void PointingAtFreeSpaceFindsNoNode()
    {
        var tree = Folder();
        var free = tree.SizeOf(tree.RootNode) * 3;
        var surface = Treemap(tree, tree.RootNode, ExploreView.Treemap, free);

        var hit = Assert.Single(Hits(surface).Where(hit => hit.IsFreeSpace).Distinct());

        Assert.False(hit.IsNode);
        Assert.False(hit.IsAggregate);
        Assert.Equal(free, hit.Bytes);
    }

    [Fact]
    public void FreeSpaceIsNeverOutlined()
    {
        var tree = Folder();
        var surface = Treemap(tree, tree.RootNode, ExploreView.Treemap, tree.SizeOf(tree.RootNode) * 3);

        Assert.Empty(surface.Outlines(new HashSet<int> { ExploreTile.FreeSpace }));
    }

    [Fact]
    public void FreeSpaceIsLabelledAndPaintedFlatInItsOwnColour()
    {
        var tree = Folder();
        var surface = Treemap(tree, tree.RootNode, ExploreView.Treemap, tree.SizeOf(tree.RootNode) * 3);

        Assert.Contains(surface.Labels, label => label.Node == ExploreTile.FreeSpace);

        var pixels = new byte[PixelBuffer.LengthFor(Width, Height)];
        surface.Paint(pixels, new TileColour(0, 0, 0));

        // The block is the larger share, so it is laid first, along the left of a wide canvas.
        // Flat is one colour from corner to corner, lit as evenly as the aggregate is.
        var corner = At(pixels, 2, 2);
        var across = At(pixels, Width / 3, Height - 3);
        var lit = CushionShading.LightAt(0, 0);

        Assert.Equal(corner, across);
        Assert.Equal((byte)(TilePalette.FreeSpace.Red * lit), corner.Red);
        Assert.Equal(corner.Red, corner.Green);
        Assert.Equal(corner.Green, corner.Blue);
    }

    [Fact]
    public void TheWholeOfAVolumeIsGivenItsFreeSpace()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", freeBytes: 500);

        Assert.Equal(500, VolumeFreeSpace.Beside(volumes, @"D:\"));
    }

    /// <summary>§6.3: a root in the extended-length form is the same volume.</summary>
    [Fact]
    public void AVolumeNamedInTheExtendedLengthFormIsStillTheWholeOfIt()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", freeBytes: 500);

        Assert.Equal(500, VolumeFreeSpace.Beside(volumes, @"\\?\D:\"));
    }

    [Fact]
    public void AFolderOnAVolumeIsGivenNoFreeSpace()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", freeBytes: 500);

        Assert.Equal(0, VolumeFreeSpace.Beside(volumes, @"D:\work"));
    }

    /// <summary>
    /// A volume mounted at a folder is the whole of itself there, and the folder picker hands the
    /// path back without the separator the inventory reports it with.
    /// </summary>
    [Fact]
    public void AVolumeMountedAtAFolderIsTheWholeOfItselfThere()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\", freeBytes: 100)
            .With(@"C:\Mount\", freeBytes: 700);

        Assert.Equal(700, VolumeFreeSpace.Beside(volumes, @"C:\Mount"));
    }

    [Fact]
    public void AnyOfAVolumesMountPointsIsTheWholeOfIt()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\", freeBytes: 100)
            .With(@"E:\", freeBytes: 900, alsoMountedAt: [@"C:\Data\"]);

        Assert.Equal(900, VolumeFreeSpace.Beside(volumes, @"C:\Data"));
    }

    [Fact]
    public void AVolumeThatWouldNotSayIsGivenNoFreeSpace()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\");

        Assert.Equal(0, VolumeFreeSpace.Beside(volumes, @"D:\"));
    }

    [Fact]
    public void ALocationOnNoKnownVolumeIsGivenNoFreeSpace()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", freeBytes: 500);

        Assert.Equal(0, VolumeFreeSpace.Beside(volumes, @"\\server.test\share"));
    }

    private static ExploreSurface Treemap(ExploreTree tree, int root, ExploreView view, long freeBytes) =>
        ExploreSurface.Create(
            tree, root, view, Width, Height, scale: 1, ShapeColours.ByBranch, ExploreSpacing.Comfortable, freeBytes);

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

    /// <summary>A folder of files under the root, so there is a folder to open.</summary>
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
