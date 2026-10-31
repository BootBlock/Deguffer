using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The colour a map's canvas stands on: the theme's, never one fixed colour (§6.5), and a high contrast
/// theme's own window colour whenever there is one.
/// </summary>
public sealed class MapGroundTests
{
    private static readonly ContrastColours Contrast = new(new TileColour(0, 0, 0), new TileColour(255, 255, 0));

    [Fact]
    public void The_dark_theme_stands_on_the_dark_ground()
    {
        Assert.Equal(new TileColour(32, 32, 32), MapGround.For(dark: true, contrast: null));
    }

    [Fact]
    public void The_light_theme_stands_on_the_light_ground()
    {
        Assert.Equal(new TileColour(243, 243, 243), MapGround.For(dark: false, contrast: null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_high_contrast_theme_stands_on_its_own_window_colour_in_either_app_theme(bool dark)
    {
        Assert.Equal(Contrast.Window, MapGround.For(dark, Contrast));
    }
}
