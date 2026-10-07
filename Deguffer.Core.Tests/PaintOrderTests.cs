using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A redraw puts its regions on screen in this order, so the order decides what the reader sees
/// first, and the cut decides whether every pixel is ever put up at all.
/// </summary>
public sealed class PaintOrderTests
{
    /// <summary>
    /// The regions cover the canvas once each. A gap is a patch of the canvas no redraw ever puts
    /// up, which shows the last picture there for good; an overlap is a pixel two threads paint at
    /// once. A size that divides by nothing the region size does leaves a ragged last row and column.
    /// </summary>
    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(1000, 700)]
    [InlineData(256, 256)]
    [InlineData(1, 1)]
    public void TheRegionsTileTheCanvasExactly(int width, int height)
    {
        var covered = new int[width * height];

        foreach (var region in PaintOrder.Regions(width, height, focus: null))
        {
            Assert.True(region.Width is > 0 and <= PaintOrder.RegionSize, $"{region} is not a region's width");
            Assert.True(region.Height is > 0 and <= PaintOrder.RegionSize, $"{region} is not a region's height");

            for (var y = region.Y; y < region.Bottom; y++)
            {
                for (var x = region.X; x < region.Right; x++)
                {
                    covered[(y * width) + x]++;
                }
            }
        }

        Assert.All(covered, count => Assert.Equal(1, count));
    }

    /// <summary>The region under the pointer is first, wherever the pointer is, centre or corner.</summary>
    [Theory]
    [InlineData(10, 10)]
    [InlineData(3830, 2150)]
    [InlineData(1920, 1080)]
    public void TheRegionUnderThePointerIsFirst(float x, float y)
    {
        var first = PaintOrder.Regions(3840, 2160, new ExplorePoint(x, y))[0];

        Assert.True(first.Contains(x, y), $"{first} does not hold the pointer at {x}, {y}");
    }

    /// <summary>
    /// After the pointer's, the regions go outwards from the middle of the canvas, where the largest
    /// shapes are, so none is put up before one nearer the middle.
    /// </summary>
    [Fact]
    public void TheRestGoOutwardsFromTheMiddle()
    {
        var regions = PaintOrder.Regions(3840, 2160, new ExplorePoint(10, 10));

        var distances = regions.Skip(1).Select(region =>
        {
            var dx = region.X + (region.Width / 2.0) - 1920;
            var dy = region.Y + (region.Height / 2.0) - 1080;

            return (dx * dx) + (dy * dy);
        }).ToList();

        Assert.Equal(distances.Order(), distances);
        Assert.True(regions[1].Contains(1920 - 1, 1080 - 1) || regions[1].Contains(1920, 1080),
            $"{regions[1]} is not at the middle of the canvas");
    }

    /// <summary>A pointer off the canvas is no reason to put any region first.</summary>
    [Fact]
    public void WithNoPointerTheMiddleIsFirst()
    {
        var first = PaintOrder.Regions(1000, 1000, focus: null)[0];

        var dx = first.X + (first.Width / 2.0) - 500;
        var dy = first.Y + (first.Height / 2.0) - 500;

        Assert.True(Math.Abs(dx) <= PaintOrder.RegionSize && Math.Abs(dy) <= PaintOrder.RegionSize, $"{first} is not at the middle");
    }
}
