using System.Numerics;
using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Composition.Interactions;

namespace Deguffer.App.Tests;

/// <summary>
/// The tracker's bounds while it is at rest. Bounds that follow its scale have the compositor work the
/// tracker, the camera and everything on the camera out again at every frame for as long as the
/// process runs, so at rest they are pinned: wide enough for any scale it rests at, and written afresh
/// whenever what they are worked out from changes, or a resize or a move of the origin would leave the
/// tracker outside them and it would spring back by itself.
/// </summary>
public sealed class MapCameraBoundsTests
{
    private const float Width = 300;

    private const float Height = 100;

    /// <summary>Twice the least ceiling, which the pinned bounds are worked out for.</summary>
    private const float Pinned = (float)(2 * MapCeiling.Least);

    [Fact]
    public void ATrackerStartsAtRestWithItsBoundsPinnedForTwiceTheCeiling()
    {
        var (pinned, least, most) = CompositionThread.Run(() =>
        {
            var (camera, tracker) = Bounded();

            return (camera.BoundsPinned, tracker.MinPosition, tracker.MaxPosition);
        });

        Assert.True(pinned);
        Assert.Equal(Vector3.Zero, least);
        Assert.Equal(new Vector3((Pinned - 1) * Width, (Pinned - 1) * Height, 0), most);
    }

    /// <summary>
    /// While the origin moves the pinned bounds take in both origins, as the expressions do, because the
    /// tracker may be measured from either; once it settles, the new origin's alone.
    /// </summary>
    [Fact]
    public void PinnedBoundsFollowAMoveOfTheOrigin()
    {
        var (moving, settled) = CompositionThread.Run(() =>
        {
            var (camera, tracker) = Bounded();

            camera.Rebase(new MapOrigin(0.25, 0.5));
            var moving = (tracker.MinPosition, tracker.MaxPosition);

            camera.Settle();

            return (moving, (tracker.MinPosition, tracker.MaxPosition));
        });

        Assert.Equal(new Vector3(-0.25f * Pinned * Width, -0.5f * Pinned * Height, 0), moving.MinPosition);
        Assert.Equal(new Vector3((Pinned - 1) * Width, (Pinned - 1) * Height, 0), moving.MaxPosition);

        Assert.Equal(new Vector3(-0.25f * Pinned * Width, -0.5f * Pinned * Height, 0), settled.MinPosition);
        Assert.Equal(new Vector3(((0.75f * Pinned) - 1) * Width, ((0.5f * Pinned) - 1) * Height, 0), settled.MaxPosition);
    }

    [Fact]
    public void PinnedBoundsFollowTheSizeAndTheCeiling()
    {
        var most = CompositionThread.Run(() =>
        {
            var (camera, tracker) = Bounded();

            camera.Resize(600, 200);
            camera.Limit(256);

            return tracker.MaxPosition;
        });

        Assert.Equal(new Vector3(511 * 600, 511 * 200, 0), most);
    }

    /// <summary>
    /// Bounds following the scale are the expressions' alone: a pinned value written over them would
    /// hold a tracker that a hand is moving to the scale it rested at.
    /// </summary>
    [Fact]
    public void UnpinnedBoundsAreNotWrittenAndArePinnedAfreshAtTheNextRest()
    {
        var (unpinned, whileMoving, rested) = CompositionThread.Run(() =>
        {
            var (camera, tracker) = Bounded();

            camera.UnpinBounds();
            var unpinned = !camera.BoundsPinned;

            camera.Resize(600, 200);
            var whileMoving = tracker.MaxPosition;

            camera.PinBounds();

            return (unpinned, whileMoving, tracker.MaxPosition);
        });

        Assert.True(unpinned);
        Assert.Equal(new Vector3((Pinned - 1) * Width, (Pinned - 1) * Height, 0), whileMoving);
        Assert.Equal(new Vector3((Pinned - 1) * 600, (Pinned - 1) * 200, 0), rested);
    }

    private static (MapCamera Camera, InteractionTracker Tracker) Bounded()
    {
        var compositor = CompositionThread.Compositor;
        var camera = new MapCamera(compositor);
        var tracker = InteractionTracker.Create(compositor);

        camera.Resize(Width, Height);
        camera.Bound(tracker);

        return (camera, tracker);
    }
}
