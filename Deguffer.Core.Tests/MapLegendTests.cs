using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>What each colour on a map means, for each way of colouring it.</summary>
public sealed class MapLegendTests
{
    /// <summary>A hue per branch explains itself, so a map coloured by branch has no legend in any scheme.</summary>
    [Theory]
    [InlineData(ExploreScheme.Standard)]
    [InlineData(ExploreScheme.Vivid)]
    public void BranchesHaveNoLegend(ExploreScheme scheme)
    {
        Assert.Empty(MapLegend.For(ExploreColouring.Branch, scheme));
    }

    /// <summary>
    /// The age legend is in the scheme the map is drawn in: a legend in one set of colours beside a
    /// map in another names every band wrongly.
    /// </summary>
    [Fact]
    public void TheAgeLegendIsInTheMapsScheme()
    {
        var bands = AgePalette.Bands(ExploreScheme.Vivid);

        Assert.Equal(
            bands.Select(band => (band.Label, band.Colour)),
            MapLegend.For(ExploreColouring.Age, ExploreScheme.Vivid).Select(band => (band.Label, band.Colour)));
        Assert.NotEqual(
            MapLegend.For(ExploreColouring.Age, ExploreScheme.Standard).Select(band => band.Colour),
            MapLegend.For(ExploreColouring.Age, ExploreScheme.Vivid).Select(band => band.Colour));
    }

    /// <summary>Growth, kinds of file and depth have one scale each, the same in every scheme.</summary>
    [Fact]
    public void TheOtherScalesAreTheirPalettesBands()
    {
        Assert.Equal(
            GrowthPalette.Bands.Select(band => (band.Label, band.Colour)),
            MapLegend.For(ExploreColouring.Growth, ExploreScheme.Soft).Select(band => (band.Label, band.Colour)));
        Assert.Equal(
            TypePalette.Bands.Select(band => (band.Label, band.Colour)),
            MapLegend.For(ExploreColouring.Type, ExploreScheme.Deep).Select(band => (band.Label, band.Colour)));
        Assert.Equal(
            DepthPalette.Bands.Select(band => (band.Label, band.Colour)),
            MapLegend.For(ExploreColouring.Depth, ExploreScheme.Standard).Select(band => (band.Label, band.Colour)));
    }
}
