using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A canvas large enough to be cut into bands and shaded on several threads has to come out as the
/// picture one thread would have produced.
///
/// <para>Every canvas the app actually draws is that size — the threshold is half a megapixel and a
/// 1080p panel is two — so this is the shipped path rather than an edge of it, and the two ways it
/// can go wrong are both invisible to a small-canvas test. A band that measures a shape's cushion
/// from its own top edge puts a seam across the picture at every boundary, and a band split that
/// does not tile the canvas exactly leaves rows nobody painted.</para>
/// </summary>
public sealed class ParallelPaintingTests
{
    // Comfortably past PixelBuffer's threshold, and a height that divides evenly by nothing the
    // band arithmetic is likely to pick — so a rounding mistake in the split shows up as a gap
    // rather than being hidden by a clean division.
    private const int Width = 1024;
    private const int Height = 769;

    private static readonly TileColour Ground = TileColour.FromRgb(0x123456);

    /// <summary>
    /// The bands together cover every row exactly once. A gap between two of them is a stripe of
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
    /// The cushion is measured across the whole rectangle, not across the band drawing part of it.
    /// Measured per band, the gradient restarts at every boundary and the rectangle comes out as a
    /// stack of ridges — which reads as nesting that is not there.
    /// </summary>
    [Fact]
    public void ARectangleSpanningEveryBandIsShadedAsOneCushion()
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
    /// Painting order survives the split. Each band draws every rectangle in the order the layout
    /// gave, so a child still covers its parent — in every band, not only the one holding the row a
    /// small test happens to look at.
    /// </summary>
    [Fact]
    public void ALaterRectangleCoversAnEarlierOneInEveryBandItReaches()
    {
        var under = new ExploreTile(1, 0, 1, 0, 0, Width, Height);

        // An aggregate on top, because it is the one shape drawn flat: every row of it is the same
        // colour, so a band that painted it before the rectangle underneath — or skipped it — shows
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

        TileRasteriser.Paint(
            pixels, tiles, Width, Height, Ground,
            (node, depth) => node == ExploreTile.Aggregated
                ? TilePalette.Aggregate
                : TilePalette.For(node, depth));

        return pixels;
    }

    private static TileColour At(byte[] pixels, int x, int y)
    {
        var offset = ((y * Width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
