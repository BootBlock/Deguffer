namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// The camera's move from origin <paramref name="From"/> to <paramref name="To"/>, made by asking the
/// interaction tracker for request <paramref name="Request"/>, which puts it on
/// <paramref name="Viewport"/> measured from the new origin. It lasts until the tracker answers that
/// request: by carrying it out or by refusing it.
///
/// <para>The tracker answers on the compositor, a frame or more after it is asked, and in the order it
/// did things. So until the answer, every report it sends is from before the move and measured from
/// the old origin: a hand's included, which may come in between. A hand's report says nothing about
/// whether the move will be carried out. Touch holds the tracker, which then refuses the request, but
/// a turn of the wheel or the touchpad only coasts it, and the request is carried out after the coast
/// has started.</para>
/// </summary>
public readonly record struct MapOriginMove(long Request, MapOrigin From, MapOrigin To, MapViewport Viewport)
{
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
}
