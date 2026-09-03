using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// §6.5: the UI has to read correctly on a flat background in either theme, and a treemap puts text
/// over whatever colour the rectangle underneath happens to be. These are the two rules that makes
/// possible — the ground comes from the caller rather than from a constant, and the label colour is
/// computed per rectangle rather than fixed.
/// </summary>
public sealed class TileRenderingTests
{
    private const int Width = 64;
    private const int Height = 48;

    private static readonly TileColour Ground = TileColour.FromRgb(0x123456);

    [Fact]
    public void TheBufferIsFullyOpaqueAndTheRightSize()
    {
        var pixels = Paint([], Width, Height, Ground, _ => 0);

        Assert.Equal(Width * Height * 4, pixels.Length);

        for (var i = 3; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i]);
        }
    }

    [Fact]
    public void WhereNothingIsDrawnTheCallersGroundShowsThrough()
    {
        var pixels = Paint([], Width, Height, Ground, _ => 0);

        Assert.Equal(Ground.Blue, pixels[0]);
        Assert.Equal(Ground.Green, pixels[1]);
        Assert.Equal(Ground.Red, pixels[2]);
    }

    [Fact]
    public void ARectangleIsPaintedWhereItWasLaidOutAndNowhereElse()
    {
        var tile = new ExploreTile(Node: 1, Depth: 1, Bytes: 1, X: 10, Y: 10, Width: 20, Height: 20);

        var pixels = Paint([tile], Width, Height, Ground, _ => 0);

        Assert.NotEqual(Ground, At(pixels, 15, 15));
        Assert.Equal(Ground, At(pixels, 5, 5));
        Assert.Equal(Ground, At(pixels, 40, 40));
    }

    /// <summary>
    /// Cushion shading is light on one colour, not a second colour. If it changed the hue, two
    /// branches would stop being separable by their hue — which is the whole colour scheme.
    /// </summary>
    [Fact]
    public void ShadingChangesTheBrightnessOfARectangleWithoutChangingItsHue()
    {
        var tile = new ExploreTile(Node: 1, Depth: 0, Bytes: 1, X: 0, Y: 0, Width: Width, Height: Height);
        var expected = TilePalette.For(0, 0);

        var pixels = Paint([tile], Width, Height, Ground, _ => 0);

        var centre = At(pixels, Width / 2, Height / 2);
        var corner = At(pixels, 1, 1);

        Assert.True(centre.RelativeLuminance > corner.RelativeLuminance, "the cushion was flat");

        // The channels keep their order and their rough proportion; only the level moved.
        Assert.True(centre.Red > centre.Blue == expected.Red > expected.Blue);
        Assert.True(centre.Green > centre.Blue == expected.Green > expected.Blue);
    }

    [Fact]
    public void AnAggregateIsDrawnInItsOwnColourRatherThanABranchHue()
    {
        var tile = new ExploreTile(ExploreTile.Aggregated, Depth: 1, Bytes: 99, X: 0, Y: 0, Width: Width, Height: Height);

        var pixels = Paint([tile], Width, Height, Ground, _ => 0);

        // Flat, and neutral: an aggregate is not a thing on the disk, so it gets neither a cushion
        // nor a hue that would give it the same presence as the files it stands in for.
        var centre = At(pixels, Width / 2, Height / 2);

        Assert.Equal(At(pixels, 2, 2), centre);
        Assert.Equal(centre.Red, centre.Green);
        Assert.Equal(centre.Green, centre.Blue);
    }

    [Fact]
    public void ALaterRectangleCoversAnEarlierOne()
    {
        var under = new ExploreTile(1, 0, 1, 0, 0, Width, Height);
        var over = new ExploreTile(2, 1, 1, 4, 4, 8, 8);

        var pixels = Paint([under, over], Width, Height, Ground, n => n);

        Assert.NotEqual(At(pixels, 6, 6), At(pixels, 30, 30));
    }

    [Fact]
    public void OnlyARectangleWithRoomForTextIsOfferedALabel()
    {
        var limits = LayoutLimits.Default;

        Assert.True(new ExploreTile(1, 1, 1, 0, 0, 120, 40).HasRoomForALabel(limits));
        Assert.False(new ExploreTile(1, 1, 1, 0, 0, 120, 8).HasRoomForALabel(limits));
        Assert.False(new ExploreTile(1, 1, 1, 0, 0, 20, 40).HasRoomForALabel(limits));

        // Scaled with the display, or a high-DPI screen labels rectangles half the intended size.
        Assert.False(new ExploreTile(1, 1, 1, 0, 0, 60, 20).HasRoomForALabel(limits.At(2)));
        Assert.True(new ExploreTile(1, 1, 1, 0, 0, 120, 40).HasRoomForALabel(limits.At(2)));
    }

    /// <summary>
    /// The fixed-label-colour bug this exists to prevent: white text is legible on the palette's
    /// blue and illegible on its yellow, and both are in the same picture.
    /// </summary>
    [Fact]
    public void LabelColourIsWhicheverOfBlackAndWhiteContrastsMore()
    {
        Assert.Equal(new TileColour(0, 0, 0), TilePalette.For(3, 0).ContrastingText);      // yellow
        Assert.Equal(new TileColour(255, 255, 255), TilePalette.For(4, 0).ContrastingText); // blue

        foreach (var branch in Enumerable.Range(0, 8))
        {
            var colour = TilePalette.For(branch, 0);
            var chosen = colour.ContrastingText;

            var chosenContrast = Contrast(colour, chosen);
            var other = chosen.Red == 0 ? new TileColour(255, 255, 255) : new TileColour(0, 0, 0);

            Assert.True(chosenContrast >= Contrast(colour, other), $"branch {branch} took the worse label colour");
        }
    }

    /// <summary>
    /// Hue says which branch and lightness says how deep. Two branches that came out the same
    /// colour would make the map claim a relationship that is not there.
    /// </summary>
    [Fact]
    public void EveryBranchGetsADistinctColour()
    {
        var colours = Enumerable.Range(0, 8).Select(b => TilePalette.For(b, 0)).ToList();

        Assert.Equal(8, colours.Distinct().Count());
    }

    [Fact]
    public void DepthChangesLightnessAndKeepsTheBranchRecognisable()
    {
        var shallow = TilePalette.For(1, 0);
        var deeper = TilePalette.For(1, 1);

        Assert.NotEqual(shallow, deeper);
        Assert.True(deeper.RelativeLuminance > shallow.RelativeLuminance);

        // Four steps, then it repeats — long enough to separate a parent from its child, short
        // enough that the eighth level is not white.
        Assert.Equal(shallow, TilePalette.For(1, 4));
    }

    /// <summary>
    /// A branch number past the palette wraps rather than throwing. A directory can hold any number
    /// of children, and an exception from a repaint handler takes the window down.
    /// </summary>
    [Fact]
    public void ABranchNumberBeyondThePaletteWrapsRatherThanFailing()
    {
        Assert.Equal(TilePalette.For(0, 0), TilePalette.For(8, 0));
        Assert.Equal(TilePalette.For(1, 0), TilePalette.For(9, 0));
    }

    private static double Contrast(TileColour a, TileColour b)
    {
        var (lighter, darker) = a.RelativeLuminance >= b.RelativeLuminance
            ? (a.RelativeLuminance, b.RelativeLuminance)
            : (b.RelativeLuminance, a.RelativeLuminance);

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Paint into a buffer this test owns. The rasteriser writes into a caller-supplied array
    /// rather than allocating one, because at 4K that array is 33 MB and the view repaints it
    /// several times a second.
    /// </summary>
    private static byte[] Paint(IReadOnlyList<ExploreTile> tiles, int width, int height, TileColour ground, Func<int, int> branchOf)
    {
        var pixels = new byte[PixelBuffer.LengthFor(width, height)];

        // These tests are about where the pixels go, so they colour the way the shipped map does
        // and let the branch stand in for the whole scheme. AgeColouringTests asks the other
        // question, which is what a colour means.
        TileRasteriser.Paint(
            pixels, tiles, width, height, ground,
            (node, depth) => node == ExploreTile.Aggregated
                ? TilePalette.Aggregate
                : TilePalette.For(branchOf(node), depth));

        return pixels;
    }

    private static TileColour At(byte[] pixels, int x, int y)
    {
        var offset = ((y * Width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
