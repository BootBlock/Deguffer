using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The overview a zoomed map shows in a corner: its size, the rectangle round the screen, where a
/// click or a drag on it takes the camera, and the corner it moves to so it never covers what the
/// map is being acted on at.
/// </summary>
public sealed class MapOverviewTests
{
    private const int Ceiling = 64;

    /// <summary>Zoomed four times, showing the quarter of the picture across from a quarter in and halfway down.</summary>
    private static readonly MapViewport Zoomed = MapViewport.Anchored(4, 0.25, 0.5, 0, 0, Ceiling);

    [Fact]
    public void TheOverviewHasTheMapsShapeAndIsNoLongerThanItsSide()
    {
        Assert.Equal((200, 112.5), MapOverview.Size(1600, 900));
    }

    [Fact]
    public void OnASmallMapTheOverviewTakesAQuarterOfItAtMost()
    {
        Assert.Equal((100, 75), MapOverview.Size(400, 300));
    }

    /// <summary>Too small to aim at, whether the whole map is small or only one side of it.</summary>
    [Theory]
    [InlineData(160, 120)]
    [InlineData(3000, 300)]
    [InlineData(0, 300)]
    public void AMapTooSmallForAnOverviewHasNone(double width, double height)
    {
        Assert.Null(MapOverview.Size(width, height));
    }

    [Fact]
    public void TheRectangleIsThePartOfThePictureOnTheScreen()
    {
        Assert.Equal(new MapFrame(0.25, 0.5, 0.25, 0.25), MapOverview.Frame(Zoomed, 200, 100));
    }

    /// <summary>
    /// Deep in, the part on the screen is far under a pixel of the overview. The rectangle stays
    /// large enough to see and press, about the middle of that part, on each axis by the overview's
    /// own size on it.
    /// </summary>
    [Fact]
    public void DeepInTheRectangleKeepsItsLeastSizeAboutTheMiddleOfTheScreen()
    {
        var deep = MapViewport.Anchored(1000, 0.5, 0.5, 0, 0, 1000);

        var frame = MapOverview.Frame(deep, 200, 100);

        Assert.Equal(MapOverview.LeastFrame / 200, frame.Width, 12);
        Assert.Equal(MapOverview.LeastFrame / 100, frame.Height, 12);
        Assert.Equal(0.5 + (0.5 / 1000), frame.Centre.X, 12);
        Assert.Equal(0.5 + (0.5 / 1000), frame.Centre.Y, 12);
    }

    /// <summary>A click flies the camera there at the zoom it is at: it moves the screen, and never magnifies.</summary>
    [Fact]
    public void ACentredCameraHasThePointInTheMiddleOfTheScreenAtTheSameZoom()
    {
        var centred = MapOverview.Centred(Zoomed, 0.6, 0.3, Ceiling);

        Assert.Equal(4, centred.Zoom);
        Assert.Equal(0.475, centred.Left, 12);
        Assert.Equal(0.175, centred.Top, 12);
    }

    [Fact]
    public void ACentredCameraNearAnEdgeStaysInsideThePicture()
    {
        var centred = MapOverview.Centred(Zoomed, 0.99, 0.01, Ceiling);

        Assert.Equal(0.75, centred.Left, 12);
        Assert.Equal(0, centred.Top, 12);
    }

    [Fact]
    public void ADraggedRectangleTakesTheScreenAsFarAsItWent()
    {
        var dragged = MapOverview.Dragged(Zoomed, 0.1, -0.2);

        Assert.Equal(4, dragged.Zoom);
        Assert.Equal(0.35, dragged.Left, 12);
        Assert.Equal(0.3, dragged.Top, 12);
    }

    [Fact]
    public void ADraggedRectangleStopsAtThePicturesEdge()
    {
        var dragged = MapOverview.Dragged(Zoomed, 0.9, 0.9);

        Assert.Equal(0.75, dragged.Left, 12);
        Assert.Equal(0.75, dragged.Top, 12);
    }

    /// <summary>
    /// On a map 1600 by 900 the overview is 200 by 112.5, standing 12 in from the top-right corner:
    /// from 1388 to 1588 across and 12 to 124.5 down. The map acted on there, or within reach of it, moves
    /// it to the other corner.
    /// </summary>
    [Theory]
    [InlineData(1500, 100)]
    [InlineData(1388 - MapOverview.Reach + 1, 100)]
    [InlineData(1500, 124.5 + MapOverview.Reach - 1)]
    public void ActedOnNearItTheOverviewMovesToTheOtherCorner(double x, double y)
    {
        Assert.Equal(MapCorner.TopLeft, MapOverview.Avoiding(MapCorner.TopRight, x, y, 1600, 900));
    }

    [Theory]
    [InlineData(800, 450)]
    [InlineData(1388 - MapOverview.Reach - 1, 100)]
    [InlineData(1500, 124.5 + MapOverview.Reach + 1)]
    [InlineData(100, 50)]
    public void ActedOnAwayFromItTheOverviewStaysWhereItIs(double x, double y)
    {
        Assert.Equal(MapCorner.TopRight, MapOverview.Avoiding(MapCorner.TopRight, x, y, 1600, 900));
    }

    [Fact]
    public void TheOverviewMovesBackOutOfTheLeftCornerTheSameWay()
    {
        Assert.Equal(MapCorner.TopRight, MapOverview.Avoiding(MapCorner.TopLeft, 100, 50, 1600, 900));
    }
}
