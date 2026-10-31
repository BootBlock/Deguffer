using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition.Interactions;

namespace Deguffer.App.Controls;

/// <summary>
/// The requests that move a map's camera, put to the compositor's interaction tracker in its own
/// terms: single-precision pixels of the screen, and key frame animations. Each says which request it
/// made, for <see cref="MapRequests"/> to follow. What to ask for is Core's
/// (<see cref="MapTrackerMove"/>, <see cref="MapPull"/>, <see cref="MapFling"/>).
/// </summary>
internal static class TrackerMoves
{
    /// <summary>Put the tracker on <paramref name="position"/> at once.</summary>
    public static int Place(InteractionTracker tracker, MapTracking position) => tracker.TryUpdatePosition(Position(position));

    /// <summary>Stop the tracker where it is.</summary>
    public static int Halt(InteractionTracker tracker) => tracker.TryUpdatePositionBy(Vector3.Zero);

    /// <summary>
    /// Set the tracker coasting at the speed (<paramref name="speed"/>) a hand dragging the picture had,
    /// in pixels a second. The picture moves with the hand, so the screen moves the other way.
    /// </summary>
    public static int Fling(InteractionTracker tracker, (double X, double Y) speed) =>
        tracker.TryUpdatePositionWithAdditionalVelocity(new Vector3((float)-speed.X, (float)-speed.Y, 0));

    /// <summary>
    /// Make <paramref name="move"/> at once, in one request where it has a still point. See
    /// <see cref="MapTrackerMove.Rescales"/> for why the scale and the position are asked for together
    /// only where it has none.
    /// </summary>
    public static int Jump(InteractionTracker tracker, MapTrackerMove move)
    {
        var scale = (float)move.To.Zoom;

        if (move.Pivot is { } pivot)
        {
            return tracker.TryUpdateScale(scale, Point(pivot));
        }

        if (move.Rescales)
        {
            tracker.TryUpdateScale(scale, Vector3.Zero);
        }

        return Place(tracker, move.Position);
    }

    /// <summary>
    /// Play <paramref name="move"/> as <paramref name="motion"/> says: a scale about its still point
    /// through the key frames of <see cref="MapGlide"/>, or a pan where it has none. Never for an
    /// instant motion: the compositor refuses a key frame animation shorter than a millisecond.
    /// </summary>
    public static int Glide(InteractionTracker tracker, MapTrackerMove move, Motion motion)
    {
        var compositor = tracker.Compositor;

        if (move.Pivot is { } pivot)
        {
            var linear = compositor.LinearEase();
            var zoom = compositor.CreateScalarKeyFrameAnimation();

            // From wherever the tracker is when the move starts, which a report on its way may not
            // have told the map yet.
            zoom.InsertExpressionKeyFrame(0, "this.StartingValue");

            foreach (var (time, value) in new MapGlide(move.From, move.To).Zooms().Skip(1))
            {
                zoom.InsertKeyFrame((float)time, (float)value, linear);
            }

            zoom.Duration = motion.Duration;

            return tracker.TryUpdateScaleWithAnimation(zoom, Point(pivot));
        }

        var pan = compositor.CreateVector3KeyFrameAnimation();

        pan.InsertKeyFrame(1, Position(move.Position), compositor.MotionEase());
        pan.Duration = motion.Duration;

        return tracker.TryUpdatePositionWithAnimation(pan);
    }

    private static Vector3 Position(MapTracking tracking) => new((float)tracking.X, (float)tracking.Y, 0);

    private static Vector3 Point((double X, double Y) point) => new((float)point.X, (float)point.Y, 0);
}
