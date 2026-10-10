using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The map's camera in the interaction tracker's terms, and back. The tracker is what moves the
/// picture, and every click is resolved through the viewport read back from it (§7.1), so a mapping
/// that drifted would put a pick on a shape the screen does not show there.
/// </summary>
public sealed class MapTrackingTests
{
    private const double Width = 1600;

    private const double Height = 900;

    private const double Precision = 1e-9;

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(4, 0.25, 0.5)]
    [InlineData(64, 0.9, 0.1)]
    public void AViewportHeldByTheTrackerReadsBackAsItself(double zoom, double pictureX, double pictureY)
    {
        var viewport = MapViewport.Anchored(zoom, pictureX, pictureY, 0.5, 0.5);
        var tracking = MapTracking.Of(viewport, Width, Height);

        AssertSame(viewport, tracking.Shown(Width, Height, elastic: true));
        AssertSame(viewport, tracking.Shown(Width, Height, elastic: false));
    }

    /// <summary>
    /// A pinch or the wheel scales about the point under the hand, and the tracker moves its position
    /// to keep that point still. Read back, the thing under the pointer is still under it, and the
    /// viewport is the one a zoom at the pointer asks for.
    /// </summary>
    [Theory]
    [InlineData(1, 0.3, 0.6, 3)]
    [InlineData(2, 0.7, 0.2, 5.5)]
    [InlineData(16, 0.5, 0.5, 12)]
    public void AZoomAboutThePointerKeepsWhatIsUnderItUnderIt(double zoom, double screenX, double screenY, double scale)
    {
        var from = MapViewport.Anchored(zoom, 0.45, 0.55, 0.5, 0.5);
        var under = from.PictureAt(screenX, screenY);

        var scaled = ScaledAbout(MapTracking.Of(from, Width, Height), scale, screenX * Width, screenY * Height);
        var shown = scaled.Shown(Width, Height, elastic: true);

        var (x, y) = shown.PictureAt(screenX, screenY);

        Assert.Equal(under.X, x, Precision);
        Assert.Equal(under.Y, y, Precision);
        AssertSame(MapViewport.Anchored(scale, under.X, under.Y, screenX, screenY), shown);
    }

    /// <summary>
    /// A glide is played as a scale about one still point, and the position is the tracker's to
    /// follow. If the point were wrong the zoom would arrive and the picture would be somewhere else.
    /// </summary>
    [Theory]
    [InlineData(1, 0.5, 0.5, 8, 0.2, 0.3)]
    [InlineData(8, 0.2, 0.3, 1, 0.5, 0.5)]
    [InlineData(2, 0.1, 0.9, 40, 0.95, 0.05)]
    public void ScalingAboutThePivotArrivesWhereTheGlideWasGoing(
        double fromZoom, double fromX, double fromY, double toZoom, double toX, double toY)
    {
        var from = MapViewport.Anchored(fromZoom, fromX, fromY, 0.5, 0.5);
        var to = MapViewport.Anchored(toZoom, toX, toY, 0.5, 0.5);

        var pivot = MapTracking.Pivot(from, to, Width, Height);

        Assert.NotNull(pivot);

        var arrived = ScaledAbout(MapTracking.Of(from, Width, Height), to.Zoom, pivot.Value.X, pivot.Value.Y);

        AssertSame(to, arrived.Shown(Width, Height, elastic: true));
    }

    /// <summary>
    /// No zoom, or so little that the still point is far beyond where the tracker can place it, is a
    /// pan. Scaled about a point that far away, the single-precision tracker would land a long way off.
    /// </summary>
    [Fact]
    public void AMoveThatBarelyZoomsHasNoStillPointAndIsAPan()
    {
        var from = MapViewport.Anchored(4, 0.2, 0.2, 0.5, 0.5);
        var panned = from.Panned(-0.5, 0);
        var barely = MapViewport.Anchored(4 * (1 + 1e-4), 0.7, 0.2, 0.5, 0.5);

        Assert.Null(MapTracking.Pivot(from, panned, Width, Height));
        Assert.Null(MapTracking.Pivot(from, barely, Width, Height));
        Assert.NotNull(MapTracking.Pivot(from, MapViewport.Anchored(4.4, 0.7, 0.2, 0.5, 0.5), Width, Height));
    }

    /// <summary>
    /// Only a rounding error counts as no zoom. A zoom of a tenth of a percent about the screen's
    /// centre keeps that point still, and played as a pan it would slide the picture under it instead.
    /// </summary>
    [Fact]
    public void ASmallZoomStillHasItsStillPoint()
    {
        var from = MapViewport.Anchored(4, 0.3, 0.3, 0.5, 0.5);
        var to = MapViewport.Anchored(4 * 1.001, 0.3, 0.3, 0.5, 0.5);

        var pivot = MapTracking.Pivot(from, to, Width, Height);

        Assert.NotNull(pivot);
        Assert.Equal(0.5 * Width, pivot.Value.X, 1e-6);
        Assert.Equal(0.5 * Height, pivot.Value.Y, 1e-6);
    }

    /// <summary>
    /// A hand stretching the picture past its edge, or a pinch past the zoom's limits, is on screen as
    /// it is, so a click then lands on what the screen shows rather than where it will settle.
    /// </summary>
    [Fact]
    public void AStretchedPictureIsShownStretched()
    {
        var zoom = 4.0;
        var tracking = new MapTracking(-120, (zoom - 1) * Height + 80, zoom);

        var shown = tracking.Shown(Width, Height, elastic: true);

        Assert.True(shown.Left < 0, $"the screen's left edge came back at {shown.Left}");
        Assert.True(shown.Top > 1 - (1 / zoom), $"the screen's top edge came back at {shown.Top}");
    }

    /// <summary>
    /// With animation effects off the camera follows the tracker held to its limits, the zoom first
    /// and the position at that zoom, and the screen shows that, so a click is resolved against it.
    /// Held the other way round, a pinch past the maximum would place the picture as if at the larger
    /// zoom, a little away from where the camera shows it.
    /// </summary>
    [Fact]
    public void WithStretchingOffTheScreenShowsTheTrackerHeldToItsLimits()
    {
        var max = MapViewport.MaximumZoom;
        var over = max * 1.1;
        var tracking = new MapTracking(0.5 * (over - 1) * Width, -40, over);

        var shown = tracking.Shown(Width, Height, elastic: false);

        Assert.Equal(max, shown.Zoom, Precision);
        Assert.Equal(tracking.X / (max * Width), shown.Left, Precision);
        Assert.Equal(0, shown.Top, Precision);
    }

    /// <summary>
    /// A drag past an edge is split into what the tracker holds and what the camera stretches. What
    /// the tracker holds has to be inside its bounds at the held scale, or the tracker keeps a
    /// position the camera then never shows back inside.
    /// </summary>
    [Theory]
    [InlineData(-300, 200, 3, 0, 200, 3)]
    [InlineData(5000, -10, 3, 2 * Width, 0, 3)]
    [InlineData(10, 10, 0.8, 0, 0, 1)]
    [InlineData(1e6, 1e6, 100, (MapViewport.MaximumZoom - 1) * Width, (MapViewport.MaximumZoom - 1) * Height, MapViewport.MaximumZoom)]
    public void HeldIsInsideTheTrackersBoundsAtTheHeldScale(
        double x, double y, double scale, double heldX, double heldY, double heldScale)
    {
        var held = new MapTracking(x, y, scale).Held(Width, Height);

        Assert.Equal(new MapTracking(heldX, heldY, heldScale), held);
    }

    /// <summary>
    /// The tracker's own rule for a scale about a point, as it documents it: the point stays where it
    /// is on the screen, so the position grows with the scale about it.
    /// </summary>
    private static MapTracking ScaledAbout(MapTracking tracking, double scale, double x, double y) => new(
        (scale / tracking.Scale * (tracking.X + x)) - x,
        (scale / tracking.Scale * (tracking.Y + y)) - y,
        scale);

    private static void AssertSame(MapViewport expected, MapViewport actual)
    {
        Assert.Equal(expected.Zoom, actual.Zoom, Precision);
        Assert.Equal(expected.Left, actual.Left, Precision);
        Assert.Equal(expected.Top, actual.Top, Precision);
    }
}
