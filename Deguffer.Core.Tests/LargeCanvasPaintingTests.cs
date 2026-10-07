using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A canvas large enough to be cut into many regions, each painted on its own, has to come out as
/// one picture.
///
/// <para>Every canvas the app actually draws is that size, so this is the shipped path rather than an
/// edge of it, and the two ways it can go wrong are both invisible to a canvas of one region. A
/// region that measures a shape's cushion from its own edge puts a seam across the picture at every
/// boundary, and regions that do not tile the canvas exactly leave pixels nobody painted.</para>
/// </summary>
public sealed class LargeCanvasPaintingTests
{
    // Several regions across and down, and a height that is not a multiple of the region size, so
    // the last row of regions is ragged.
    private const int Width = 1024;
    private const int Height = 769;

    private static readonly TileColour Ground = TileColour.FromRgb(0x123456);

    /// <summary>
    /// The regions together cover every pixel exactly once. A gap between two of them is a patch of
    /// whatever the buffer held before, which on a reused buffer is the previous frame.
    /// </summary>
    [Fact]
    public void EveryPixelOfAnEmptyCanvasIsTheGround()
    {
        var pixels = Paint([]);

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                Assert.Equal(Ground, At(pixels, x, y));
            }
        }
    }

    /// <summary>
    /// The cushion is measured across the whole rectangle, not across the region drawing part of it.
    /// Measured per region, the gradient restarts at every boundary and the rectangle comes out as a
    /// stack of ridges — which reads as nesting that is not there.
    /// </summary>
    [Fact]
    public void ARectangleSpanningEveryRegionIsShadedAsOneCushion()
    {
        var pixels = Paint([new ExploreTile(Node: 1, Depth: 0, Bytes: 1, X: 0, Y: 0, Width, Height)]);

        var turns = 0;
        var direction = 0;
        var previous = At(pixels, Width / 2, 0).RelativeLuminance;

        for (var y = 1; y < Height; y++)
        {
            var current = At(pixels, Width / 2, y).RelativeLuminance;
            var step = Math.Sign(current - previous);

            if (step != 0)
            {
                if (direction != 0 && step != direction)
                {
                    turns++;
                }

                direction = step;
                previous = current;
            }
        }

        // One: a cushion has a single ridge, so brightness climbs to it and falls away after.
        Assert.Equal(1, turns);
    }

    /// <summary>
    /// Painting order survives the split. Each region draws every rectangle in the order the layout
    /// gave, so a child still covers its parent — in every region, not only the one holding the
    /// pixel a small test happens to look at.
    /// </summary>
    [Fact]
    public void ALaterRectangleCoversAnEarlierOneInEveryRegionItReaches()
    {
        var under = new ExploreTile(1, 0, 1, 0, 0, Width, Height);

        // An aggregate on top, because it is the one shape drawn flat: every row of it is the same
        // colour, so a region that painted it before the rectangle underneath — or skipped it — shows
        // up as that row carrying the cushion instead.
        var over = new ExploreTile(ExploreTile.Aggregated, 1, 1, 100, 0, 200, Height);

        var pixels = Paint([under, over]);
        var covered = At(pixels, 200, 0);

        Assert.NotEqual(Ground, covered);

        for (var y = 0; y < Height; y++)
        {
            Assert.Equal(covered, At(pixels, 200, y));
            Assert.NotEqual(covered, At(pixels, 700, y));
        }
    }

    private static byte[] Paint(IReadOnlyList<ExploreTile> tiles)
    {
        var pixels = new byte[PixelBuffer.LengthFor(Width, Height)];

        new TileRasteriser(
            tiles, Width, Height, Ground,
            (node, depth) => node == ExploreTile.Aggregated
                ? TilePalette.Aggregate
                : Hues.Colour(node, depth))
            .PaintRegions(pixels);

        return pixels;
    }

    private static TileColour At(byte[] pixels, int x, int y)
    {
        var offset = ((y * Width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
