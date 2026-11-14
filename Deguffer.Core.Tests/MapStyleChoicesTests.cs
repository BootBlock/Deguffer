using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The choices of a style that depend on another: a bevel's width is as many display pixels as asked,
/// and the window greys a box exactly where the style says it changes nothing. A preset sets the finish
/// and leaves the reader's light and detail as they were.
/// </summary>
public sealed class MapStyleChoicesTests
{
    /// <summary>A bevel is one, two or three pixels wide at 100%, and as heavy at 200%.</summary>
    [Theory]
    [InlineData(MapBevelWidth.Narrow, 1)]
    [InlineData(MapBevelWidth.Medium, 2)]
    [InlineData(MapBevelWidth.Wide, 3)]
    public void ABevelIsAsWideAsAsked(MapBevelWidth width, int pixels)
    {
        var style = MapStyle.Default with { Shading = MapShading.Bevel, BevelWidth = width };

        Assert.Equal(pixels, ShapeFinish.Of(style, 1).Bevel.Width);
        Assert.Equal(pixels * 2, ShapeFinish.Of(style, 2).Bevel.Width);
    }

    /// <summary>A bevel's width is nothing where the shapes are not bevelled.</summary>
    [Fact]
    public void ABevelsWidthIsNothingWithoutABevel() =>
        Assert.Equal(0, ShapeFinish.Of(MapStyle.Default with { BevelWidth = MapBevelWidth.Wide }, 1).Bevel.Width);

    /// <summary>Depth changes every shading but flat, whose shapes are their own colour.</summary>
    [Theory]
    [InlineData(MapShading.Cushion, true)]
    [InlineData(MapShading.Bevel, true)]
    [InlineData(MapShading.Gradient, true)]
    [InlineData(MapShading.Flat, false)]
    public void DepthChangesEveryShadingButFlat(MapShading shading, bool raised) =>
        Assert.Equal(raised, (MapStyle.Default with { Shading = shading }).Raised);

    /// <summary>The width, tone and sharing of a line change nothing while no line is drawn, and something once one is.</summary>
    [Fact]
    public void TheLinesSettingsChangeSomethingOnlyWhereALineIsDrawn()
    {
        var bare = MapStyle.Default;
        var lined = MapStyle.Default with { FolderBorders = MapBorder.Faint };

        Assert.False(bare.Outlined);
        Assert.True(lined.Outlined);
        Assert.Equal(ShapeFinish.Of(bare, 1), ShapeFinish.Of(bare with { BorderWidth = MapBorderWidth.Heavy, BorderTone = MapBorderTone.Light }, 1));
        Assert.NotEqual(ShapeFinish.Of(lined, 1), ShapeFinish.Of(lined with { BorderWidth = MapBorderWidth.Heavy }, 1));
    }

    /// <summary>
    /// The lettering and the sizes change something only where a name is written; the text size only
    /// in the application's lettering; how many names only where shapes are named in it.
    /// </summary>
    [Fact]
    public void TheNamesSettingsChangeSomethingOnlyWhereTheyApply()
    {
        var unnamed = MapStyle.Default with { FolderNames = false, ShapeNames = false };
        var bandsOnly = MapStyle.Default with { ShapeNames = false };
        var classic = MapStyle.Default with { Lettering = MapLettering.Classic };

        Assert.False(unnamed.Named);
        Assert.False(unnamed.OwnLettering);
        Assert.True(bandsOnly.OwnLettering);
        Assert.False(bandsOnly.NamesCounted);
        Assert.False(classic.OwnLettering);
        Assert.False(classic.NamesCounted);
        Assert.True(MapStyle.Default.NamesCounted);
    }

    /// <summary>A preset sets the finish and the spacing, and leaves the reader's light and detail as they were.</summary>
    [Fact]
    public void APresetLeavesTheLightAndTheDetail()
    {
        var detail = new MapDetail(MapSmallestShape.Tiny, MapLevels.More, MapTextSize.Large, MapNameCount.Fewer, Sizes: false);
        var light = new MapPointerLight(Reach: MapLightReach.Wide);
        var look = MapLook.Default with { Detail = detail, Light = light };

        foreach (var preset in MapPreset.All)
        {
            var applied = preset.AppliedTo(look);
            var written = preset.Into(look.Into(AppPreferences.Default));

            Assert.Equal((detail, light), (applied.Detail, applied.Light));
            Assert.Equal((detail, light), (written.MapDetail, written.MapPointerLight));
        }
    }

    /// <summary>The presets drawn from the new choices are drawn with them: strength, brightness and a bevel's width.</summary>
    [Fact]
    public void ThePresetsUseTheNewChoices()
    {
        Assert.Equal(MapSaturation.Grey, MapPreset.Monochrome.Style.Saturation);
        Assert.Equal((MapSaturation.Muted, MapBrightness.Lighter), (MapPreset.Pastel.Style.Saturation, MapPreset.Pastel.Style.Brightness));
        Assert.Equal((MapSaturation.Vivid, MapBrightness.Darker), (MapPreset.Dusk.Style.Saturation, MapPreset.Dusk.Style.Brightness));
        Assert.Equal(MapBevelWidth.Wide, MapPreset.Tiles.Style.BevelWidth);
    }
}
