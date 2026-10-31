using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// How the interaction tracker is asked to move the camera. A scale and a position asked for one after
/// the other in the same frame can leave the position wrong, so a move with a still point is one scale
/// about it, and the two are asked for together only where there is none.
/// </summary>
public sealed class MapTrackerMoveTests
{
    private const double Width = 1600;

    private const double Height = 900;

    private static readonly MapOrigin Origin = new(0.25, 0.5);

    [Fact]
    public void AZoomIsAScaleAboutItsStillPoint()
    {
        var from = MapViewport.Seen(2, 0.25, 0.25);
        var to = MapViewport.Seen(8, 0.4, 0.3);

        var move = MapTrackerMove.Between(from, to, Width, Height, Origin);

        Assert.NotNull(move.Pivot);
        Assert.Equal(MapTracking.Pivot(from, to, Width, Height), move.Pivot);
        Assert.False(move.Rescales, "a zoom with a still point asked for the scale and the position apart");
        Assert.Equal(MapTracking.Of(to, Width, Height, Origin), move.Position);
    }

    [Fact]
    public void AMoveAtOneZoomIsAPan()
    {
        var move = MapTrackerMove.Between(MapViewport.Seen(4, 0.1, 0.1), MapViewport.Seen(4, 0.3, 0.2), Width, Height, Origin);

        Assert.Null(move.Pivot);
        Assert.False(move.Rescales, "a pan asked for a scale as well");
    }

    /// <summary>A zoom that barely changes has its still point too far off the screen, so it changes the scale and then the position.</summary>
    [Fact]
    public void AZoomThatBarelyChangesRescalesAndThenPans()
    {
        var move = MapTrackerMove.Between(MapViewport.Seen(4, 0.1, 0.1), MapViewport.Seen(4 * (1 + 1e-6), 0.3, 0.2), Width, Height, Origin);

        Assert.Null(move.Pivot);
        Assert.True(move.Rescales, "a zoom without a still point left its scale unchanged");
    }
}
