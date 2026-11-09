using System.Runtime.CompilerServices;
using Deguffer.App.Controls;
using Microsoft.UI.Composition.Interactions;

namespace Deguffer.App.Tests;

/// <summary>
/// A map's zoom goes with the map that holds it. Its interaction tracker and its settle timer are
/// native objects that hold what they call back with a plain COM reference, which the collector
/// treats as a root, so a zoom either of them reached was kept alive, and its map with it, for the
/// life of the process (#737).
/// </summary>
public sealed class ExploreZoomRetentionTests
{
    [Fact]
    public async Task AZoomNothingHoldsIsCollectedWhileItsCameraAndTrackerLive()
    {
        var (zoom, camera, tracker) = CompositionThread.Run(Build);

        // The tracker is handed the camera's bounds and the jump the resize asked for at the next
        // commit, so it is a live tracker, not a request still to be sent, that is asked to let go.
        await CompositionThread.Run(() => CompositionThread.Compositor.RequestCommitAsync()).AsTask();

        Assert.True(Collector.Reclaims(zoom));
        GC.KeepAlive(camera);
        GC.KeepAlive(tracker);
    }

    /// <summary>Not inlined, so nothing on the test's own stack holds the zoom.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Zoom, MapCamera Camera, InteractionTracker Tracker) Build()
    {
        var compositor = CompositionThread.Compositor;
        var camera = new MapCamera(compositor);
        var zoom = new ExploreZoom(compositor.CreateContainerVisual(), camera, new ScriptedMotion(true), CompositionThread.Queue);

        zoom.Resize(800, 600);

        return (new WeakReference(zoom), camera, zoom.Tracker);
    }
}
