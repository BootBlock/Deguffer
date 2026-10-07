using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A drawing painted a region at a time, in the order a redraw puts the regions on screen, is the
/// same picture as the drawing painted whole.
///
/// <para>What can go wrong is at the regions' edges. A rasteriser that measures a cushion across the
/// part of a shape inside a region puts a seam at every region boundary; one that clips a shape to
/// the region's rows but not its columns paints into the region beside it, which another thread is
/// painting at the same time; and one that indexes the claimed pixels from the canvas's left edge
/// rather than the region's leaves shapes out of every region but the first column.</para>
/// </summary>
public sealed class RegionPaintingTests
{
    // Not a multiple of the region size either way, so the last row and column are ragged.
    private const int Width = 700;
    private const int Height = 530;

    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void RegionByRegionIsTheWholePicture(ExploreView view) =>
        AssertSamePicture(Drawn(view, MapViewport.Whole));

    /// <summary>A zoomed treemap's shapes run off the canvas, so its cushions are measured past every edge.</summary>
    [Fact]
    public void AZoomedTreemapRegionByRegionIsTheWholePicture() =>
        AssertSamePicture(Drawn(ExploreView.Treemap, MapViewport.Anchored(3, 0.4, 0.6, 0.5, 0.5)));

    /// <summary>
    /// A region's painter writes inside the region and nowhere else. Painting every region in turn
    /// cannot show a painter that strays, because what it writes next door is the right colour and
    /// is painted over again; but the region next door is being painted by another thread at the
    /// same moment, and may already have been put on screen.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void APainterWritesNothingOutsideItsRegion(ExploreView view)
    {
        var painter = Drawn(view, MapViewport.Whole).Painter(Drawings.Ground);

        // The middle of the canvas, where every view draws, and away from every edge.
        var region = new CanvasRegion(X: 250, Y: 200, Width: 180, Height: 120);
        var pixels = new byte[PixelBuffer.LengthFor(Width, Height)];

        Array.Fill(pixels, (byte)0x5A);
        painter.Paint(pixels, region);

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (!region.Contains(x, y))
                {
                    Assert.True(
                        pixels.AsSpan(((y * Width) + x) * 4, 4).IndexOfAnyExcept((byte)0x5A) < 0,
                        $"the pixel at {x}, {y} is outside the region and was written");
                }
            }
        }
    }

    private static void AssertSamePicture(ExploreSurface drawing)
    {
        var painter = drawing.Painter(Drawings.Ground);

        var whole = new byte[PixelBuffer.LengthFor(Width, Height)];
        painter.PaintRegions(whole);

        // Every byte starts as something no painter writes, so a region left unpainted shows.
        var regions = new byte[whole.Length];
        Array.Fill(regions, (byte)0x5A);

        foreach (var region in PaintOrder.Regions(Width, Height, new ExplorePoint(Width - 1, 0)))
        {
            painter.Paint(regions, region);
        }

        var differing = Enumerable.Range(0, whole.Length / 4).FirstOrDefault(
            pixel => !whole.AsSpan(pixel * 4, 4).SequenceEqual(regions.AsSpan(pixel * 4, 4)),
            -1);

        Assert.True(
            differing < 0,
            $"the pixel at {differing % Width}, {differing / Width} differs between the two paints");
    }

    private static ExploreSurface Drawn(ExploreView view, MapViewport viewport) => ExploreSurface.Create(
        NestedTree(),
        ExploreTreeBuilder.RootNode,
        view,
        Width,
        Height,
        scale: 1,
        textScale: 1,
        ShapeColours.ByBranch(ExploreScheme.Standard),
        ExploreSpacing.Comfortable,
        VolumeSpace.None,
        viewport);

    /// <summary>Folders of files of mixed sizes, three levels deep, so shapes nest across region edges.</summary>
    private static ExploreTree NestedTree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var folders = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, 5).Select(i => new ExploreChild($"folder{i}", IsDirectory: true, IsLink: false, Size: 0))]);

        for (var folder = folders; folder < folders + 5; folder++)
        {
            var inner = builder.AddChildren(
                folder,
                [.. Enumerable.Range(0, 3).Select(i => new ExploreChild($"inner{i}", IsDirectory: true, IsLink: false, Size: 0))]);

            for (var directory = inner; directory < inner + 3; directory++)
            {
                builder.AddChildren(
                    directory,
                    [.. Enumerable.Range(0, 7).Select(i =>
                        new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000L * (i + 1) * (directory % 4 + 1)))]);
            }
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
