namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// The camera's move from origin <see cref="From"/> to <see cref="To"/>, made by asking the
/// interaction tracker for request <see cref="Request"/>, which puts it on <see cref="Viewport"/>
/// measured from the new origin. It lasts until the tracker answers that request: by carrying it out
/// or by refusing it.
///
/// <para>The tracker answers on the compositor, a frame or more after it is asked, and in the order it
/// did things. So until the answer, every report it sends is from before the move and measured from
/// the old origin: a hand's included, which may come in between. A hand's report says nothing about
/// whether the move will be carried out. Touch holds the tracker, which then refuses the request, but
/// a turn of the wheel or the touchpad only coasts it, and the request is carried out after the coast
/// has started.</para>
///
/// <para>A class rather than a record because what the tracker reports before it answers changes
/// what follows the answer (<see cref="HeardRest"/>).</para>
/// </summary>
public sealed class MapOriginMove(long request, MapOrigin from, MapOrigin to, MapViewport viewport)
{
    /// <summary>The request that asks the tracker to the same place measured from <see cref="To"/>.</summary>
    public long Request { get; } = request;

    /// <summary>The origin the camera was measured from before the move.</summary>
    public MapOrigin From { get; } = from;

    /// <summary>The origin the camera is measured from once the tracker carries the move out.</summary>
    public MapOrigin To { get; } = to;

    /// <summary>Where the camera is, which the move does not change.</summary>
    public MapViewport Viewport { get; } = viewport;

    /// <summary>Whether a hand's coast came to rest before the tracker answered.</summary>
    public bool RestedBeforeAnswer { get; private set; }

    /// <summary>
    /// Whether a report or a rest caused by request <paramref name="id"/> shows the tracker carried the
    /// move out. Requests are numbered upwards from one and a hand is zero, so a report of this request
    /// or of a later one comes after it.
    /// </summary>
    public bool CarriedOutBy(long id) => id >= Request;

    /// <summary>Whether the tracker refusing request <paramref name="id"/> is it refusing the move.</summary>
    public bool RefusedBy(long id) => id == Request;

    /// <summary>The origin a report caused by request <paramref name="id"/> is measured from.</summary>
    public MapOrigin MeasuredFor(long id) => CarriedOutBy(id) ? To : From;

    /// <summary>
    /// The tracker came to rest for request <paramref name="id"/>, from before the move, while the map
    /// was <paramref name="coasting"/>: following a hand's coast. A coast's rest is a hand's, so it
    /// counts only for a hand's request, and only while the map was following it.
    /// </summary>
    public void HeardRest(long id, bool coasting) => RestedBeforeAnswer |= id == 0 && coasting;

    /// <summary>
    /// What follows the tracker carrying the move out, where the map was <paramref name="coasting"/>:
    /// following a wheel or a touchpad that began to coast before the move.
    ///
    /// <para>The move stopped that coast where the move went, so the tracker is there, and the camera
    /// comes to rest there unless the coast had already rested: a tracker at rest does not report
    /// coming to rest again. Where the map was following a request of its own, that request's reports
    /// say what follows, and the move changes nothing.</para>
    /// </summary>
    public MapOriginCarried Carried(bool coasting) =>
        !coasting ? MapOriginCarried.Unchanged : RestedBeforeAnswer ? MapOriginCarried.Rests : MapOriginCarried.Coasts;
}
