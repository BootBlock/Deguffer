using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Tests.Fakes;

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
        var expected = Hues.Colour(0, 0);

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
    /// The fixed-label-colour bug this exists to prevent: white text is legible on a dark shape and
    /// illegible on a light one, and both are in the same picture. The age ramp's darkest band and a
    /// dark shape of any kind need white; its brightest needs black.
    /// </summary>
    [Fact]
    public void LabelColourIsWhicheverOfBlackAndWhiteContrastsMore()
    {
        var black = new TileColour(0, 0, 0);
        var white = new TileColour(255, 255, 255);

        Assert.Equal(white, TileColour.FromRgb(0x440154).ContrastingText);
        Assert.Equal(white, TileColour.FromRgb(0x303040).ContrastingText);
        Assert.Equal(black, TileColour.FromRgb(0xFDE725).ContrastingText);
    }

    /// <summary>
    /// And the branch palette's own colours, in every scheme, round the whole circle and at every
    /// depth it distinguishes, lifted and not. The lighter schemes take black throughout and the
    /// deep one takes white near the root, so this is the rule held over every palette rather than
    /// the rule itself.
    /// </summary>
    public static TheoryData<ExploreScheme> Schemes => [.. Enum.GetValues<ExploreScheme>()];

    [Theory]
    [MemberData(nameof(Schemes))]
    public void EveryBranchColourTakesTheLabelColourThatContrastsMore(ExploreScheme scheme)
    {
        var black = new TileColour(0, 0, 0);
        var white = new TileColour(255, 255, 255);

        for (var hue = 0; hue < 360; hue += 15)
        {
            for (var depth = 0; depth <= 6; depth++)
            {
                foreach (var lifted in new[] { false, true })
                {
                    var colour = TilePalette.For(new BranchHue(hue, 10, lifted), depth, scheme);
                    var chosen = colour.ContrastingText;
                    var other = chosen == black ? white : black;

                    Assert.True(
                        Contrast(colour, chosen) >= Contrast(colour, other),
                        $"hue {hue} at depth {depth} took the worse label colour");
                }
            }
        }
    }

    /// <summary>
    /// Lightness says how deep. A child drawn no lighter than its parent would read as a sibling, and
    /// the ramp stops after a few levels so the deepest shapes are not white.
    /// </summary>
    [Theory]
    [MemberData(nameof(Schemes))]
    public void DepthRaisesLightnessForAFewLevelsAndThenHolds(ExploreScheme scheme)
    {
        var hue = new BranchHue(200, 20, Lifted: false);

        for (var depth = 1; depth < 5; depth++)
        {
            Assert.True(
                TilePalette.For(hue, depth + 1, scheme).RelativeLuminance > TilePalette.For(hue, depth, scheme).RelativeLuminance,
                $"depth {depth + 1} was not lighter than depth {depth}");
        }

        Assert.Equal(TilePalette.For(hue, 5, scheme), TilePalette.For(hue, 9, scheme));
    }

    /// <summary>
    /// Alternate siblings are a step lighter, so two neighbours whose hues are close still differ in
    /// the one property a colour-vision deficiency leaves intact.
    /// </summary>
    [Theory]
    [MemberData(nameof(Schemes))]
    public void ALiftedSiblingIsLighterThanItsNeighbourAtTheSameHue(ExploreScheme scheme)
    {
        var plain = TilePalette.For(new BranchHue(120, 20, Lifted: false), 2, scheme);
        var lifted = TilePalette.For(new BranchHue(120, 20, Lifted: true), 2, scheme);

        Assert.True(lifted.RelativeLuminance > plain.RelativeLuminance);
    }

    /// <summary>
    /// The drawing's root owns the whole circle and so has no hue of its own. It is drawn neutral,
    /// because it is the frame round everything else rather than a branch of it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Schemes))]
    public void TheRootIsANeutralGrey(ExploreScheme scheme)
    {
        var root = TilePalette.For(BranchHue.Whole, 0, scheme);

        Assert.Equal(root.Red, root.Green);
        Assert.Equal(root.Green, root.Blue);
    }

    /// <summary>
    /// Free space is neither a thing on the disk nor a run of them, so it is kept apart from the
    /// aggregate's colour as well as from every hue.
    /// </summary>
    [Fact]
    public void FreeSpaceIsANeutralApartFromTheAggregate()
    {
        var free = TilePalette.FreeSpace;

        Assert.NotEqual(TilePalette.Aggregate, free);
        Assert.Equal(free.Red, free.Green);
        Assert.Equal(free.Green, free.Blue);
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
                : Hues.Colour(branchOf(node), depth));

        return pixels;
    }

    private static TileColour At(byte[] pixels, int x, int y)
    {
        var offset = ((y * Width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
