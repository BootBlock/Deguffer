using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A zoom the compositor plays: key frames of the scale, about the move's still point. Every frame has
/// to be a step of the one path along which the thing under the pointer stays still, so the picture
/// never swings sideways on its way.
/// </summary>
public sealed class MapGlideTests
{
    private const double Width = 1600;

    private const double Height = 900;

    private const double Precision = 1e-9;

    private static readonly MapViewport From = MapViewport.Anchored(2, 0.4, 0.45, 0.5, 0.5);

    private static readonly MapViewport To = MapViewport.Anchored(24, 0.3, 0.7, 0.35, 0.65);

    [Fact]
    public void AGlideStartsAtTheZoomOnScreenAndEndsAtTheOneAskedFor()
    {
        var frames = new MapGlide(From, To).Zooms();

        Assert.Equal(MapGlide.Steps + 1, frames.Count);
        Assert.Equal((0.0, From.Zoom), frames[0]);
        Assert.Equal(1, frames[^1].Time, Precision);
        Assert.Equal(To.Zoom, frames[^1].Zoom, Precision);
    }

    /// <summary>
    /// Eased out, so the picture answers the wheel at once and settles rather than lagging behind it,
    /// and it never turns back on its way.
    /// </summary>
    [Fact]
    public void AGlideIsFastestAtTheStartAndNeverTurnsBack()
    {
        var frames = new MapGlide(MapViewport.Whole, To).Zooms();
        var halfway = frames[MapGlide.Steps / 2];

        Assert.True(
            halfway.Zoom > MapViewport.Between(MapViewport.Whole, To, 0.5).Zoom,
            "the glide was not eased out");

        for (var step = 1; step < frames.Count; step++)
        {
            Assert.True(frames[step].Time > frames[step - 1].Time);
            Assert.True(frames[step].Zoom > frames[step - 1].Zoom, $"the zoom went back at step {step}");
        }
    }

    /// <summary>
    /// The tracker scales about the still point and keeps it still, so each frame shows the viewport
    /// that zoom puts there. That has to be where the move should be at that moment, which is the path
    /// <see cref="MapViewport.Between"/> describes at the eased progress. A zoom eased by equal steps
    /// rather than equal ratios would leave it at every frame but the two ends.
    ///
    /// <para>The same from any origin the tracker is measured from: a scale about a point is one rule
    /// wherever the position counts from.</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.6, 0.1)]
    public void EveryFrameIsOnThePathFromOneViewportToTheOther(double originLeft, double originTop)
    {
        var pivot = MapTracking.Pivot(From, To, Width, Height)!.Value;
        var start = MapTracking.Of(From, Width, Height, new MapOrigin(originLeft, originTop));

        foreach (var (time, zoom) in new MapGlide(From, To).Zooms())
        {
            var shown = new MapTracking(
                (zoom / start.Scale * (start.X + pivot.X)) - pivot.X,
                (zoom / start.Scale * (start.Y + pivot.Y)) - pivot.Y,
                zoom,
                start.Origin).Shown(Width, Height, elastic: true);

            var expected = MapViewport.Between(From, To, Motion.Ease(time));

            Assert.Equal(expected.Zoom, shown.Zoom, Precision);
            Assert.Equal(expected.Left, shown.Left, Precision);
            Assert.Equal(expected.Top, shown.Top, Precision);
        }
    }
}
