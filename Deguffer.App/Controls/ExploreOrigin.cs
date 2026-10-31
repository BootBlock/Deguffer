using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Composition.Interactions;

namespace Deguffer.App.Controls;

/// <summary>
/// The point of a map's picture its camera is measured from (see <see cref="MapOrigin"/>), and the
/// camera's move to a new one, from asking the tracker until it answers.
///
/// <para>The camera and every placement change together, on the UI thread: the camera is held where
/// it is, measured from the new origin, and the map places everything from it again. The tracker is
/// then asked to the same place measured from the new origin, and the camera follows it again once it
/// answers (<see cref="Carried"/>, <see cref="Refused"/>). The two cannot change in the same frame,
/// because the tracker answers on the compositor. Which report answers the move, and what follows the
/// answer, are Core's (<see cref="MapOriginMove"/>), and <see cref="ExploreZoom"/> acts on them.</para>
/// </summary>
internal sealed class ExploreOrigin(InteractionTracker tracker, MapCamera camera, MapRequests requests)
{
    /// <summary>
    /// Raised when the camera starts measuring from a new <see cref="Origin"/>, or goes back to the old
    /// one. See <see cref="ExploreZoom.OriginMoved"/>.
    /// </summary>
    public event EventHandler? Moved;

    /// <summary>What the tracker and every placement are measured from.</summary>
    public MapOrigin Origin { get; private set; }

    /// <summary>The camera's move to a new origin, until the tracker answers it, and null otherwise.</summary>
    public MapOriginMove? Pending { get; private set; }

    /// <summary>The origin a report caused by request <paramref name="id"/> is measured from.</summary>
    public MapOrigin MeasuredFor(long id) => Pending is { } move ? move.MeasuredFor(id) : Origin;

    /// <summary>
    /// The camera follows the tracker, stretching past its limits only where <paramref name="elastic"/>.
    /// A camera held while its origin moves follows it again, as it then asks, once the tracker answers.
    /// </summary>
    public void Follow(bool elastic)
    {
        if (Pending is null)
        {
            camera.Track(tracker, elastic);
        }
    }

    /// <summary>
    /// Measure from <paramref name="shown"/>, on a screen <paramref name="width"/> by
    /// <paramref name="height"/>, without moving anything on it. The caller has checked the camera is at
    /// rest there, far enough from <see cref="Origin"/>, with no move of the origin still unanswered.
    /// </summary>
    public void Move(MapViewport shown, double width, double height)
    {
        var from = Origin;

        Origin = MapOrigin.At(shown);
        camera.Hold(shown.Camera(width, height, Origin));
        camera.Rebase(Origin);
        Moved?.Invoke(this, EventArgs.Empty);

        var request = TrackerMoves.Place(tracker, MapTracking.Of(shown, width, height, Origin));

        Pending = new MapOriginMove(request, from, Origin, shown);
        requests.Asked(request, shown);
    }

    /// <summary>
    /// The tracker carried the move out, and is measured from the new origin: the camera follows it
    /// again, stretching past its limits only where <paramref name="elastic"/>.
    /// </summary>
    public void Carried(bool elastic) => Answered(elastic);

    /// <summary>
    /// The tracker refused <paramref name="move"/>, because a hand holds the camera, and is still
    /// measured from the old origin: everything goes back to it, and the camera follows the tracker
    /// again, stretching past its limits only where <paramref name="elastic"/>.
    /// </summary>
    public void Refused(MapOriginMove move, bool elastic)
    {
        Origin = move.From;
        camera.Rebase(Origin);
        Answered(elastic);
        Moved?.Invoke(this, EventArgs.Empty);
    }

    private void Answered(bool elastic)
    {
        Pending = null;
        camera.Settle();
        camera.Track(tracker, elastic);
    }
}
