using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// How strong and how light a map's colours are: the palette's own untouched by the standard choices,
/// greys with no colour at all, more or less colour as asked, and lighter or darker towards white or
/// black. A drawing paints every shape in its tuned colour and every block in its own, and a picture
/// laid out the same whatever its colours.
/// </summary>
public sealed class ColourTuningTests
{
    private static readonly TileColour Orange = new(230, 120, 40);

    /// <summary>The standard choices leave every colour as the palette states it, so a map nobody tuned is drawn as it always was.</summary>
    [Fact]
    public void TheStandardChoicesLeaveTheColoursAlone()
    {
        Assert.Equal(ColourTuning.None, ColourTuning.Of(MapStyle.Default));
        Assert.Equal(Orange, ColourTuning.None.Apply(Orange));
    }

    /// <summary>No colour at all is a grey, the same in every channel, as light as the colour was.</summary>
    [Fact]
    public void GreyLeavesNoColour()
    {
        var grey = ColourTuning.Of(MapStyle.Default with { Saturation = MapSaturation.Grey }).Apply(Orange);

        Assert.Equal(grey.Red, grey.Green);
        Assert.Equal(grey.Green, grey.Blue);
        Assert.InRange(grey.RelativeLuminance, Orange.RelativeLuminance - 0.05, Orange.RelativeLuminance + 0.05);
    }

    /// <summary>Each step of saturation keeps more of the colour than the one before it, and vivid more than the palette's own.</summary>
    [Fact]
    public void EachSaturationKeepsMoreColourThanTheOneBeforeIt()
    {
        var spreads = Enum.GetValues<MapSaturation>()
            .Select(saturation => Spread(ColourTuning.Of(MapStyle.Default with { Saturation = saturation }).Apply(Orange)))
            .ToList();

        Assert.Equal(0, spreads[0]);
        Assert.Equal(spreads.Order(), spreads);
        Assert.Equal(spreads.Count, spreads.Distinct().Count());
        Assert.Equal(Spread(Orange), spreads[(int)MapSaturation.Standard]);
    }

    /// <summary>Lighter moves towards white and darker towards black, each step further than the one before.</summary>
    [Fact]
    public void EachBrightnessIsLighterThanTheOneBeforeIt()
    {
        var lights = Enum.GetValues<MapBrightness>()
            .Select(brightness => ColourTuning.Of(MapStyle.Default with { Brightness = brightness }).Apply(Orange).RelativeLuminance)
            .ToList();

        Assert.Equal(lights.Order(), lights);
        Assert.Equal(lights.Count, lights.Distinct().Count());
        Assert.Equal(Orange.RelativeLuminance, lights[(int)MapBrightness.Standard]);
    }

    /// <summary>A colour stronger than a screen can show is cut to what it can, never wrapped round to another.</summary>
    [Fact]
    public void AVividColourStaysOnTheScreen()
    {
        var vivid = ColourTuning.Of(MapStyle.Default with { Saturation = MapSaturation.Vivid }).Apply(new TileColour(250, 10, 5));

        Assert.Equal(255, vivid.Red);
        Assert.Equal(0, vivid.Green);
        Assert.Equal(0, vivid.Blue);
    }

    /// <summary>
    /// A drawing paints each shape in its palette colour tuned, and lays it out where it lays it out
    /// untuned: the colours change and nothing else does. A block keeps its own colour, which says
    /// what it is.
    /// </summary>
    [Fact]
    public void ADrawingPaintsEveryShapeTunedAndEveryBlockInItsOwnColour()
    {
        var style = MapStyle.Default with { Saturation = MapSaturation.Muted, Brightness = MapBrightness.Darker };
        var tuning = ColourTuning.Of(style);
        var plain = Draw(MapStyle.Default);
        var tuned = Draw(style);

        Assert.Equal(plain.Tiles, tuned.Tiles);

        // The quads of the tiles on the canvas, in the tiles' order, which is the order both drawings make them in.
        var drawn = plain.Tiles.Where(tile => CushionQuad.Of(tile, plain.Finish).Reaches(plain.Width, plain.Height, 0)).ToList();
        var before = plain.Quads().Quads.ToArray();
        var after = tuned.Quads().Quads.ToArray();

        Assert.Equal(drawn.Count, before.Length);
        Assert.Contains(drawn, tile => tile.Block is not null);
        Assert.Contains(drawn, tile => tile.Block is null && tile.Framed);

        for (var i = 0; i < before.Length; i++)
        {
            var expected = drawn[i].Block is null ? tuning.Apply(before[i].Colour) : before[i].Colour;

            Assert.Equal(expected, after[i].Colour);
        }
    }

    /// <summary>A name is written in black or white against the colour its shape is painted, tuned.</summary>
    [Fact]
    public void ANameContrastsWithItsShapeAsTuned()
    {
        var dark = Draw(MapStyle.Default with { Brightness = MapBrightness.MuchDarker });
        var light = Draw(MapStyle.Default with { Brightness = MapBrightness.MuchLighter });

        Assert.Contains(dark.Labels, label => label.Colour == new TileColour(255, 255, 255));
        Assert.Contains(light.Labels, label => label.Colour == new TileColour(0, 0, 0));
    }

    /// <summary>How far a colour's channels are apart, which is how much colour it has.</summary>
    private static int Spread(TileColour colour) =>
        Math.Max(colour.Red, Math.Max(colour.Green, colour.Blue)) - Math.Min(colour.Red, Math.Min(colour.Green, colour.Blue));

    private static TiledSurface Draw(MapStyle style) => (TiledSurface)ExploreSurface.Create(
        Tree(), ExploreTreeBuilder.RootNode, ExploreView.Treemap, 600, 400, scale: 1, textScale: 1,
        ShapeColours.ByDepth, ExploreSpacing.Comfortable, VolumeSpace.None, style: style);

    /// <summary>A folder of files beside large files, and a run of files each too small to draw, which the layout draws as one block.</summary>
    private static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder("Sample");
        var folder = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("folder", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("big.bin", IsDirectory: false, IsLink: false, Size: 30_000_000),
                new ExploreChild("large.bin", IsDirectory: false, IsLink: false, Size: 20_000_000),
                .. Enumerable.Range(0, 400).Select(i => new ExploreChild($"tiny{i}.bin", IsDirectory: false, IsLink: false, Size: 2_000)),
            ]);

        builder.AddChildren(
            folder,
            [
                new ExploreChild("a.bin", IsDirectory: false, IsLink: false, Size: 15_000_000),
                new ExploreChild("b.bin", IsDirectory: false, IsLink: false, Size: 12_000_000),
            ]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
