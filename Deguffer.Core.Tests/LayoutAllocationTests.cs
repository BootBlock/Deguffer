using System.Runtime.CompilerServices;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The layouts and the quads run once per drawing and once per tile of the map, and a full screen of
/// shapes is an array on the large object heap. Each keeps the one array it returns, of exactly the
/// shapes or quads it made, and allocates little else: no list grown a doubling at a time and copied
/// at the end, and no array sized to every shape and copied again to the ones that reach the canvas.
/// </summary>
public sealed class LayoutAllocationTests
{
    private const float Width = 3840;
    private const float Height = 2160;

    // The layout's stack of shapes still to place, the quads' own object and the like: a few kilobytes,
    // against well over a hundred for the shapes of a full screen.
    private const long Margin = 16 * 1024;

    private static readonly TileColour Grey = new(128, 128, 128);

    [Fact]
    public void ATreemapAllocatesLittleBeyondTheShapesItReturns()
    {
        var tree = WideTree();

        var (tiles, allocated) = Measure(() => TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default));

        AssertBigEnoughToGrow(tiles.Count);
        AssertWithinTheShapes(allocated, tiles.Count, Unsafe.SizeOf<ExploreTile>());
    }

    [Fact]
    public void AnIcicleAllocatesLittleBeyondTheShapesItReturns()
    {
        var tree = WideTree();
        var limits = LayoutLimits.Default with { MinimumTileSize = 0.5f };

        var (tiles, allocated) = Measure(() => IcicleLayout.Compute(tree, tree.RootNode, Width, Height, limits));

        AssertBigEnoughToGrow(tiles.Count);
        AssertWithinTheShapes(allocated, tiles.Count, Unsafe.SizeOf<ExploreTile>());
    }

    [Fact]
    public void QuadsAllocateLittleBeyondTheOnesThatReachTheCanvas()
    {
        var tree = WideTree();
        var laid = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default);

        // As many again lying off the canvas, as a zoomed picture's shapes do, so the quads that reach
        // it are half of those offered.
        ExploreTile[] tiles = [.. laid, .. laid.Select(tile => tile with { X = tile.X + (Width * 2) })];

        var (quads, allocated) = Measure(() => new CushionQuads(tiles, (int)Width, (int)Height, _ => Grey, ShapeFinish.Cushioned));

        Assert.Equal(laid.Count, quads.Quads.Length);
        AssertBigEnoughToGrow(quads.Quads.Length);
        AssertWithinTheShapes(allocated, quads.Quads.Length, Unsafe.SizeOf<CushionQuad>());
    }

    /// <summary>What a second run allocates, once the first has warmed the pool and the JIT.</summary>
    private static (T Result, long Allocated) Measure<T>(Func<T> run)
    {
        run();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = run();

        return (result, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // The fixture must lay enough shapes that a list would have grown past the large-object-heap
    // threshold, or the bound below proves nothing about that growth.
    private static void AssertBigEnoughToGrow(int count) =>
        Assert.True(count > 2_000, $"The fixture laid only {count} shapes.");

    private static void AssertWithinTheShapes(long allocated, int count, int size)
    {
        var kept = (long)count * size;

        Assert.True(
            allocated <= kept + Margin,
            $"Allocated {allocated:N0} bytes to return {count:N0} items of {kept:N0} bytes.");
    }

    /// <summary>Sixty folders of sixty files each, of sizes varied enough to give every one a shape.</summary>
    private static ExploreTree WideTree()
    {
        const int Folders = 60;
        const int Files = 60;

        var builder = new ExploreTreeBuilder(@"C:\");
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, Folders).Select(i => new ExploreChild($"dir{i}", IsDirectory: true, IsLink: false, Size: 0))]);

        for (var folder = 0; folder < Folders; folder++)
        {
            builder.AddChildren(
                first + folder,
                [.. Enumerable.Range(0, Files).Select(i => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1_000_000 + (i * 1_000)))]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
