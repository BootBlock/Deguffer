using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sunburst is painted pixel by pixel through the same index the pointer goes through, so what
/// is drawn and what is reported under the pointer cannot disagree. What is left to check here is
/// the shading: the cushion has to run round a wedge as well as across it, and it has to stop doing
/// that where a ring closes on itself and has no side to tilt away from.
/// </summary>
public sealed class SectorRenderingTests
{
    private const int Size = 81;
    private const float Centre = 40.5f;
    private const float RingWidth = 20;

    private static readonly TileColour Ground = TileColour.FromRgb(0x123456);

    [Fact]
    public void TheBufferIsFullyOpaqueAndTheGroundShowsThroughOutsideTheDisc()
    {
        var pixels = Paint(Rings(1));

        Assert.Equal(Size * Size * 4, pixels.Length);

        for (var i = 3; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i]);
        }

        Assert.Equal(Ground, At(pixels, 1, 1));
        Assert.Equal(Ground, At(pixels, Size - 2, Size - 2));
    }

    [Fact]
    public void ASectorIsPaintedWhereItWasLaidOutAndNowhereElse()
    {
        var pixels = Paint(Rings(1));

        Assert.NotEqual(Ground, At(pixels, 40, 40));
        Assert.NotEqual(Ground, At(pixels, 40, 25));

        // Just outside the outermost ring, in both directions.
        Assert.Equal(Ground, At(pixels, 40, 0));
        Assert.Equal(Ground, At(pixels, 0, 40));
    }

    /// <summary>
    /// A ring that is not full leaves the ground showing rather than something drawn over it. A gap
    /// filled with anything at all would state that space is in use when nothing said so.
    /// </summary>
    [Fact]
    public void AGapInARingIsLeftAsGround()
    {
        var sunburst = new Sunburst(
            [
                Whole(node: 0, depth: 0),
                new ExploreSector(Node: 1, Depth: 1, Bytes: 1, RingWidth, RingWidth * 2, 0, MathF.PI),
            ],
            Centre,
            Centre,
            RingWidth,
            RingWidth * 2);

        var pixels = Paint(sunburst);

        Assert.NotEqual(Ground, At(pixels, 70, 40));
        Assert.Equal(Ground, At(pixels, 10, 40));
    }

    [Fact]
    public void AnAggregateIsDrawnInItsOwnColourRatherThanABranchHue()
    {
        var sunburst = new Sunburst(
            [
                Whole(node: 0, depth: 0),
                new ExploreSector(
                    ExploreSector.Aggregated, Depth: 1, Bytes: 99, RingWidth, RingWidth * 2, 0, MathF.Tau),
            ],
            Centre,
            Centre,
            RingWidth,
            RingWidth * 2);

        var pixels = Paint(sunburst);

        // Flat, and neutral: an aggregate is not a thing on the disk, so it gets neither a cushion
        // nor a hue that would give it the same presence as the files it stands in for.
        // Just inside each edge of the ring, where a cushion is steepest, and once more at another
        // angle. All three the same is what flat means.
        var inner = Polar(pixels, radius: 21, degrees: 0);

        Assert.Equal(inner, Polar(pixels, radius: 39, degrees: 0));
        Assert.Equal(inner, Polar(pixels, radius: 30, degrees: 140));
        Assert.Equal(inner.Red, inner.Green);
        Assert.Equal(inner.Green, inner.Blue);
    }

    /// <summary>
    /// A wedge is cushioned round its width as well as across its depth, which is what gives it an
    /// edge on each side. Without the angular half a ring of wedges reads as one unbroken band.
    /// </summary>
    [Fact]
    public void AWedgeIsShadedRoundItsWidthAsWellAsAcrossItsDepth()
    {
        var quarter = MathF.Tau / 4;

        var sunburst = new Sunburst(
            [
                Whole(node: 0, depth: 0),
                new ExploreSector(Node: 1, Depth: 1, Bytes: 1, RingWidth, RingWidth * 2, 0, quarter),
                new ExploreSector(Node: 2, Depth: 1, Bytes: 1, RingWidth, RingWidth * 2, quarter, quarter),
                new ExploreSector(Node: 3, Depth: 1, Bytes: 1, RingWidth, RingWidth * 2, quarter * 2, quarter),
                new ExploreSector(Node: 4, Depth: 1, Bytes: 1, RingWidth, RingWidth * 2, quarter * 3, quarter),
            ],
            Centre,
            Centre,
            RingWidth,
            RingWidth * 2);

        // One pixel, drawn twice: once where the ring is cut into quarters and once where the same
        // ring is unbroken. Same colour, same depth, same distance out — the only difference is
        // that one of them has a side to tilt away from, and 22 degrees is far enough from the
        // middle of the quarter for that tilt to be there.
        var wedge = Polar(Paint(sunburst), radius: 30, degrees: 22);
        var unbroken = Polar(Paint(Rings(1)), radius: 30, degrees: 22);

        Assert.NotEqual(wedge, unbroken);
    }

    /// <summary>
    /// A sector that closes on itself has no angular edge, so it must not be shaded as though it
    /// had one. Shading it that way puts a seam at twelve o'clock across an unbroken ring — most
    /// visibly on the disc in the middle, which is always a whole circle.
    /// </summary>
    [Fact]
    public void AWholeRingHasNoSeamAtTwelveOClock()
    {
        var pixels = Paint(Rings(1));

        // The two pixels either side of the vertical above the middle. The canvas is an odd number
        // of pixels across, so these are the same distance out on each side of it.
        Assert.Equal(At(pixels, 39, 30), At(pixels, 41, 30));
    }

    /// <summary>
    /// Cushion shading is light on one colour, not a second colour. If it changed the hue, two
    /// branches would stop being separable by their hue — which is the whole colour scheme.
    /// </summary>
    [Fact]
    public void ShadingChangesBrightnessWithoutChangingHue()
    {
        var expected = TilePalette.For(0, 0);
        var pixels = Paint(Rings(1));

        var middle = Polar(pixels, radius: 10, degrees: 0);
        var edge = Polar(pixels, radius: 19, degrees: 180);

        Assert.True(middle.RelativeLuminance > edge.RelativeLuminance, "the cushion was flat");

        Assert.True(middle.Red > middle.Blue == expected.Red > expected.Blue);
        Assert.True(middle.Green > middle.Blue == expected.Green > expected.Blue);
    }

    /// <summary>One whole ring per level, which is what a tree with one child at each level draws.</summary>
    private static Sunburst Rings(int count)
    {
        var sectors = Enumerable.Range(0, count + 1).Select(depth => Whole(depth, depth)).ToList();

        return new Sunburst(sectors, Centre, Centre, RingWidth, (count + 1) * RingWidth);
    }

    private static ExploreSector Whole(int node, int depth) => new(
        node, depth, Bytes: 1, depth * RingWidth, (depth + 1) * RingWidth, 0, MathF.Tau);

    private static byte[] Paint(Sunburst sunburst)
    {
        var pixels = new byte[PixelBuffer.LengthFor(Size, Size)];
        SectorRasteriser.Paint(pixels, new SectorHitTest(sunburst), Size, Size, Ground, _ => 0);

        return pixels;
    }

    /// <summary>The pixel at this radius and this many degrees clockwise from twelve o'clock.</summary>
    private static TileColour Polar(byte[] pixels, float radius, float degrees)
    {
        var angle = degrees * MathF.PI / 180;

        return At(
            pixels,
            (int)(Centre + (radius * MathF.Sin(angle))),
            (int)(Centre - (radius * MathF.Cos(angle))));
    }

    private static TileColour At(byte[] pixels, int x, int y)
    {
        var offset = ((y * Size) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
