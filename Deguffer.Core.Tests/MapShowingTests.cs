using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The camera follows the keyboard: a shape the keyboard moves to that is off the screen, or too small
/// to see, is brought into view by the least move that does it, and one already in view at a size it
/// can be seen at leaves the camera where it is.
/// </summary>
public sealed class MapShowingTests
{
    private const double Precision = 1e-9;

    private const double Ceiling = 64;

    /// <summary>A shape in view and large enough leaves the camera alone.</summary>
    [Fact]
    public void AShapeInViewAndLargeEnoughLeavesTheCameraAlone()
    {
        var shown = MapViewport.Seen(zoom: 4, left: 0.25, top: 0.25);

        Assert.Equal(shown, shown.Showing(new MapFrame(0.3, 0.3, 0.05, 0.05), 0.05, 0.05, Ceiling));
        Assert.Equal(MapViewport.Whole, MapViewport.Whole.Showing(new MapFrame(0.5, 0.5, 0.2, 0.2), 0.05, 0.05, Ceiling));
    }

    /// <summary>
    /// A shape off to the right is brought in by the least pan: its far edge comes to the screen's
    /// edge, and the zoom does not change.
    /// </summary>
    [Fact]
    public void AShapeOffTheScreenIsPannedInAsLittleAsPutsItOnIt()
    {
        var shown = MapViewport.Seen(zoom: 4, left: 0.25, top: 0.25);
        var moved = shown.Showing(new MapFrame(0.6, 0.3, 0.05, 0.05), 0.05, 0.05, Ceiling);

        Assert.Equal(4, moved.Zoom, Precision);
        Assert.Equal(0.65 - 0.25, moved.Left, Precision);
        Assert.Equal(0.25, moved.Top, Precision);
    }

    /// <summary>
    /// A shape too small to see is magnified until it is the least size asked for, about its middle,
    /// and no further than the ceiling.
    /// </summary>
    [Fact]
    public void AShapeTooSmallToSeeIsMagnifiedUntilItCanBeSeen()
    {
        var part = new MapFrame(0.5, 0.5, 0.001, 0.004);
        var moved = MapViewport.Whole.Showing(part, 0.05, 0.05, Ceiling);

        Assert.Equal(50, moved.Zoom, Precision);
        Assert.True(part.X >= moved.Left && part.X + part.Width <= moved.Left + (1 / moved.Zoom));
        Assert.True(part.Y >= moved.Top && part.Y + part.Height <= moved.Top + (1 / moved.Zoom));

        Assert.Equal(Ceiling, MapViewport.Whole.Showing(new MapFrame(0.5, 0.5, 1e-6, 1e-6), 0.05, 0.05, Ceiling).Zoom, Precision);
    }

    /// <summary>
    /// The camera never zooms out to follow: a shape larger than the screen fills it, moved as little as
    /// does that.
    /// </summary>
    [Fact]
    public void AShapeLargerThanTheScreenFillsItAndTheZoomStays()
    {
        var shown = MapViewport.Seen(zoom: 8, left: 0.1, top: 0.1);
        var moved = shown.Showing(new MapFrame(0.3, 0.05, 0.4, 0.4), 0.05, 0.05, Ceiling);

        Assert.Equal(8, moved.Zoom, Precision);
        Assert.Equal(0.3, moved.Left, Precision);
        Assert.Equal(0.1, moved.Top, Precision);
    }

    /// <summary>A zoom already past the ceiling it is given is never pulled back to it.</summary>
    [Fact]
    public void AZoomPastTheCeilingIsKept()
    {
        var shown = MapViewport.Seen(zoom: 128, left: 0.5, top: 0.5);

        Assert.Equal(128, shown.Showing(new MapFrame(0.5, 0.5, 1e-7, 1e-7), 0.05, 0.05, Ceiling).Zoom, Precision);
    }
}
