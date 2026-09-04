using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A treemap's rectangles are nested, and the rasteriser draws the innermost first so that no pixel
/// is shaded twice. These pin the two halves of what that has to mean, and they pin them against a
/// rendering of the same shape on its own rather than against a repeat of the shading arithmetic —
/// so a change to the shading model cannot make them pass for the wrong reason.
///
/// <para>Run on a canvas below the threshold for splitting the work across threads and on one well
/// above it, because the record of which pixels are spoken for belongs to a band. An index that is
/// right within one band and wrong across two would pass on the small canvas alone.</para>
/// </summary>
public sealed class NestedPaintingTests
{
    private static readonly TileColour Ground = TileColour.FromRgb(0x123456);

    /// <summary>
    /// Whatever is drawn inside is drawn in full. The shape behind it must not reach a single pixel
    /// of it — which is the direction that fails if the record of spoken-for pixels reads the wrong
    /// bit, or if the rectangles are walked outermost first.
    /// </summary>
    [Theory]
    [InlineData(64, 48)]
    [InlineData(1024, 769)]
    public void AShapeInsideAnotherIsDrawnAsThoughNothingWereBehindIt(int width, int height)
    {
        var (behind, inFront) = Nested(width, height);

        var together = Paint([behind, inFront], width, height);
        var alone = Paint([inFront], width, height);

        for (var y = (int)inFront.Y; y < inFront.Y + inFront.Height; y++)
        {
            for (var x = (int)inFront.X; x < inFront.X + inFront.Width; x++)
            {
                Assert.Equal(At(alone, width, x, y), At(together, width, x, y));
            }
        }
    }

    /// <summary>
    /// The shape behind still shows everywhere the one in front does not, and that includes the
    /// single-pixel frame a directory keeps around its contents. Losing it would erase the only cue
    /// saying where one directory ends and the next begins.
    /// </summary>
    [Theory]
    [InlineData(64, 48)]
    [InlineData(1024, 769)]
    public void TheShapeBehindStillShowsWhereTheOneInFrontDoesNot(int width, int height)
    {
        var (behind, inFront) = Nested(width, height);

        var together = Paint([behind, inFront], width, height);
        var alone = Paint([behind], width, height);

        var frame = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x >= inFront.X && x < inFront.X + inFront.Width
                    && y >= inFront.Y && y < inFront.Y + inFront.Height)
                {
                    continue;
                }

                Assert.Equal(At(alone, width, x, y), At(together, width, x, y));
                frame++;
            }
        }

        // The rectangles above are chosen so that both the frame and a whole open half are left
        // showing. A test that checked nothing because the shape in front covered everything would
        // otherwise pass silently.
        Assert.True(frame > width, $"only {frame} pixels of the shape behind were left to check");
    }

    /// <summary>
    /// The record of what is spoken for is what lets the rasteriser stop walking shapes, so "full"
    /// has to mean every pixel rather than nearly every pixel. One pixel short and the rasteriser
    /// stops with the ground still showing through it.
    /// </summary>
    [Fact]
    public void ABandIsFullOnlyOnceEveryPixelOfItIsSpokenFor()
    {
        var claimed = new ClaimedPixels(width: 3, top: 4, bottom: 6);

        var pixels = (
            from y in Enumerable.Range(4, 2)
            from x in Enumerable.Range(0, 3)
            select (X: x, Y: y)).ToList();

        foreach (var (x, y) in pixels)
        {
            Assert.False(claimed.IsFull, $"full with ({x}, {y}) still to give away");

            Assert.True(claimed.Claim(x, y), $"({x}, {y}) was already spoken for");
            Assert.False(claimed.Claim(x, y), $"({x}, {y}) was given away twice");
        }

        Assert.True(claimed.IsFull);
    }

    /// <summary>
    /// A rectangle covering all but a one-pixel frame of another, down one side of the canvas — so
    /// the shape behind is left both the frame and an open half, and neither can hide a mistake in
    /// the other.
    /// </summary>
    private static (ExploreTile Behind, ExploreTile InFront) Nested(int width, int height) => (
        new ExploreTile(Node: 1, Depth: 0, Bytes: 2, X: 0, Y: 0, width, height),
        new ExploreTile(Node: 2, Depth: 1, Bytes: 1, X: 1, Y: 1, (width / 2) - 2, height - 2));

    private static byte[] Paint(IReadOnlyList<ExploreTile> tiles, int width, int height)
    {
        var pixels = new byte[PixelBuffer.LengthFor(width, height)];

        TileRasteriser.Paint(
            pixels, tiles, width, height, Ground, (node, depth) => TilePalette.For(node, depth));

        return pixels;
    }

    private static TileColour At(byte[] pixels, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
