using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>Where each key takes the map's camera, so the picture can be moved without a pointer.</summary>
public sealed class MapKeysTests
{
    private const double Precision = 1e-9;

    private static readonly MapViewport Zoomed = MapViewport.Anchored(8, 0.5, 0.5, 0.5, 0.5, MapCeiling.Least);

    /// <summary>Plus and minus zoom about the middle of the screen: what is there stays there.</summary>
    [Theory]
    [InlineData(MapKey.ZoomIn, 16)]
    [InlineData(MapKey.ZoomOut, 4)]
    public void PlusAndMinusZoomAboutTheMiddle(MapKey key, double zoom)
    {
        var from = MapViewport.Anchored(8, 0.3, 0.6, 0.5, 0.5, MapCeiling.Least);

        var to = MapKeys.Step(key, from, MapCeiling.Least);
        var (x, y) = to.PictureAt(0.5, 0.5);

        Assert.Equal(zoom, to.Zoom, Precision);
        Assert.Equal(0.3, x, Precision);
        Assert.Equal(0.6, y, Precision);
    }

    /// <summary>An arrow shows what lies that way, by a fifth of the screen.</summary>
    [Theory]
    [InlineData(MapKey.Left, -1, 0)]
    [InlineData(MapKey.Right, 1, 0)]
    [InlineData(MapKey.Up, 0, -1)]
    [InlineData(MapKey.Down, 0, 1)]
    public void AnArrowShowsWhatLiesThatWay(MapKey key, int across, int down)
    {
        var to = MapKeys.Step(key, Zoomed, MapCeiling.Least);

        Assert.Equal(Zoomed.Zoom, to.Zoom);
        Assert.Equal(Zoomed.Left + (across * MapKeys.PanStep / Zoomed.Zoom), to.Left, Precision);
        Assert.Equal(Zoomed.Top + (down * MapKeys.PanStep / Zoomed.Zoom), to.Top, Precision);
    }

    [Fact]
    public void HomeShowsTheWholePicture() => Assert.Equal(MapViewport.Whole, MapKeys.Step(MapKey.Whole, Zoomed, MapCeiling.Least));

    /// <summary>The whole picture has nowhere further out to go, and nothing beside it to pan to.</summary>
    [Theory]
    [InlineData(MapKey.ZoomOut)]
    [InlineData(MapKey.Left)]
    [InlineData(MapKey.Down)]
    public void TheWholePictureStaysWhole(MapKey key) => Assert.Equal(MapViewport.Whole, MapKeys.Step(key, MapViewport.Whole, MapCeiling.Least));
}
