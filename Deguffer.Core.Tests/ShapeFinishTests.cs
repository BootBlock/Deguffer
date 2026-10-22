using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A <see cref="MapStyle"/> made concrete for one display: how wide each line and bevel is, how far
/// each is mixed, and which shapes carry a line. What a finished picture looks like pixel by pixel is
/// asserted in <c>FinishedDrawingTests</c>.
/// </summary>
public sealed class ShapeFinishTests
{
    private static readonly TileColour Red = TileColour.FromRgb(0xFF7F7F);

    /// <summary>The style nobody has changed is the cushions the map has always been drawn with.</summary>
    [Fact]
    public void TheDefaultStyleIsTheCushionsTheMapAlwaysHad() =>
        Assert.Equal(ShapeFinish.Cushioned, ShapeFinish.Of(MapStyle.Default, pixel: 1));

    /// <summary>
    /// A mix is whole numbers out of 256, so the shader can reproduce it exactly: nothing of the tone
    /// leaves the colour as it was, the whole of it is the tone, and half is the midpoint cut down.
    /// </summary>
    [Fact]
    public void AMixIsWholeNumbersOutOf256()
    {
        var tone = new TileColour(0, 0, 0);

        Assert.Equal(Red, ShapeFinish.Mix(Red, tone, 0));
        Assert.Equal(tone, ShapeFinish.Mix(Red, tone, ShapeFinish.Whole));
        Assert.Equal(new TileColour(127, 63, 63), ShapeFinish.Mix(Red, tone, 128));
        Assert.Equal(new TileColour(255, 255, 255), ShapeFinish.Mix(Red, new TileColour(255, 255, 255), ShapeFinish.Whole));
    }

    /// <summary>
    /// Each prominence mixes further towards the tone than the one before it, and a solid line is the
    /// tone itself.
    /// </summary>
    [Fact]
    public void EachProminenceMixesFurtherTowardsTheTone()
    {
        var amounts = new[] { MapBorder.Faint, MapBorder.Medium, MapBorder.Strong, MapBorder.Solid }
            .Select(border => ShapeFinish.Of(MapStyle.Default with { BlockBorders = border }, 1).Borders.Blocks)
            .ToList();

        Assert.Equal([64, 128, 192, ShapeFinish.Whole], amounts);
    }

    /// <summary>
    /// A block's line and a folder's are set apart, so the frames of the hierarchy can be stronger or
    /// fainter than the files inside them, and a line not drawn has no width.
    /// </summary>
    [Fact]
    public void ABlocksLineAndAFoldersAreSetApart()
    {
        var finish = ShapeFinish.Of(MapStyle.Default with { BlockBorders = MapBorder.Faint, FolderBorders = MapBorder.Solid }, 1);

        Assert.Equal(64, finish.Borders.AmountFor(folder: false));
        Assert.Equal(ShapeFinish.Whole, finish.Borders.AmountFor(folder: true));

        var foldersOnly = ShapeFinish.Of(MapStyle.Default with { FolderBorders = MapBorder.Medium }, 1);

        Assert.Equal(0, foldersOnly.Borders.WidthFor(folder: false));
        Assert.Equal(1, foldersOnly.Borders.WidthFor(folder: true));
        Assert.Null(foldersOnly.EdgesOf(Red, folder: false));
        Assert.Equal(new QuadEdges(ShapeFinish.Mix(Red, new TileColour(0, 0, 0), 128), 1), foldersOnly.EdgesOf(Red, folder: true));
    }

    /// <summary>
    /// A line is a pixel at 100% and as many as the display's scale makes of one, and a thick line is
    /// twice that. A light line is mixed towards white.
    /// </summary>
    [Theory]
    [InlineData(1, MapBorderWidth.Thin, 1)]
    [InlineData(1, MapBorderWidth.Thick, 2)]
    [InlineData(2, MapBorderWidth.Thin, 2)]
    [InlineData(2, MapBorderWidth.Thick, 4)]
    public void ALineIsAsHeavyAsTheDisplaysScale(int pixel, MapBorderWidth width, int expected)
    {
        var finish = ShapeFinish.Of(
            MapStyle.Default with { BlockBorders = MapBorder.Solid, BorderWidth = width, BorderTone = MapBorderTone.Light },
            pixel);

        Assert.Equal(expected, finish.Borders.Width);
        Assert.Equal(new TileColour(255, 255, 255), finish.Borders.Tone);
    }

    /// <summary>
    /// A bevel only where the style asks for one, a pixel wide at 100%, and SpaceMonger's at the
    /// standard depth: half way to white along the top and the left.
    /// </summary>
    [Fact]
    public void ABevelIsDrawnOnlyWhereTheStyleAsksForOne()
    {
        Assert.Equal(default, ShapeFinish.Of(MapStyle.Default with { Relief = MapRelief.Strong }, 1).Bevel);

        var bevel = ShapeFinish.Of(MapStyle.Default with { Shading = MapShading.Bevel }, 2);

        Assert.Equal(2, bevel.Bevel.Width);
        Assert.Equal(TileColour.FromRgb(0xFFBFBF), bevel.Lit(Red));
        Assert.Equal(ShapeFinish.Mix(Red, new TileColour(0, 0, 0), 64), bevel.Shaded(Red));
    }

    /// <summary>A deeper relief rises further and stands out more, at every step.</summary>
    [Fact]
    public void ADeeperReliefRisesFurther()
    {
        var reliefs = Enum.GetValues<MapRelief>()
            .Select(relief => ShapeFinish.Of(MapStyle.Default with { Shading = MapShading.Bevel, Relief = relief }, 1))
            .ToList();

        for (var i = 1; i < reliefs.Count; i++)
        {
            Assert.True(reliefs[i].Relief > reliefs[i - 1].Relief);
            Assert.True(reliefs[i].Bevel.Highlight > reliefs[i - 1].Bevel.Highlight);
            Assert.True(reliefs[i].Bevel.Shadow > reliefs[i - 1].Bevel.Shadow);
        }
    }

    /// <summary>
    /// A high contrast theme's picture is flat with a line of the text colour round every shape,
    /// whatever the style: the theme's two colours replace it.
    /// </summary>
    [Fact]
    public void AHighContrastPictureIsFlatWithTheTextColourRoundEveryShape()
    {
        var text = new TileColour(255, 255, 0);
        var finish = ShapeFinish.Contrasted(new ContrastColours(new TileColour(0, 0, 0), text), 2);

        Assert.Equal(MapShading.Flat, finish.Shading);
        Assert.Equal(new ShapeBorders(text, 2, ShapeFinish.Whole, ShapeFinish.Whole), finish.Borders);
        Assert.Equal(default, finish.Bevel);
    }
}
