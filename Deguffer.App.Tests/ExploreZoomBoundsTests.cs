using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Dispatching;

namespace Deguffer.App.Tests;

/// <summary>
/// How the zoom tells the camera when the tracker is at rest. A map that is never moved, or that has
/// come to rest, keeps the tracker's bounds pinned, so the compositor has nothing to work out at any
/// frame; a hand, a glide or a fling has them follow the scale until the tracker's own rest. When the
/// tracker is at rest is Core's (<see cref="MapTrackerRest"/>).
/// </summary>
public sealed class ExploreZoomBoundsTests
{
    /// <summary>A map built and never moved, as on a page opened without a scan, costs the compositor nothing.</summary>
    [Fact]
    public void AMapNeverMovedKeepsItsBoundsPinned() =>
        Assert.True(CompositionThread.Run(() => Zoomed().Camera.BoundsPinned));

    [Fact]
    public void AHandUnpinsTheBoundsUntilItsCoastRests()
    {
        var (held, rested) = CompositionThread.Run(() =>
        {
            var (zoom, camera) = Zoomed();

            zoom.TakenByHand();
            var held = camera.BoundsPinned;

            zoom.Rested(0);

            return (held, camera.BoundsPinned);
        });

        Assert.False(held, "a hand moved the tracker with its bounds pinned at the scale it rested at");
        Assert.True(rested, "the bounds went on following the scale of a tracker at rest");
    }

    /// <summary>A hand's rest arriving after the map has set a glide off is from before the glide, which is still moving.</summary>
    [Fact]
    public void AGlideUnpinsTheBoundsAndARestFromBeforeItLeavesThemSo()
    {
        var (gliding, afterOlderRest) = CompositionThread.Run(() =>
        {
            var (zoom, camera) = Zoomed();

            zoom.Resize(300, 100);
            zoom.GlideTo(MapViewport.Anchored(4, 0.3, 0.6, 0.5, 0.5, MapCeiling.Least));
            var gliding = camera.BoundsPinned;

            zoom.Rested(0);

            return (gliding, camera.BoundsPinned);
        });

        Assert.False(gliding);
        Assert.False(afterOlderRest, "a rest from before the glide pinned the bounds while it moved");
    }

    private static (ExploreZoom Zoom, MapCamera Camera) Zoomed()
    {
        var compositor = CompositionThread.Compositor;
        var camera = new MapCamera(compositor);
        var zoom = new ExploreZoom(
            compositor.CreateContainerVisual(),
            camera,
            new ScriptedMotion(animationsEnabled: true),
            DispatcherQueue.GetForCurrentThread());

        return (zoom, camera);
    }
}
