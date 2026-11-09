namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Whether the map's interaction tracker is at rest, with nothing on its way that sets it moving by
/// itself, which is when its bounds can stop following its scale.
///
/// <para>The tracker moves by itself under a hand (a touch, a pinch, a touchpad or the wheel with Ctrl
/// held) and through a glide or a fling the map asks for. Each of those ends with the tracker reporting
/// that it has come to rest, a frame or more later and in the order it did things. A jump, a drag's
/// step, a halt or a move of the origin only puts it somewhere: from rest it stays at rest and reports
/// no new rest, and while it moves it stops it, and the rest it then reports is that request's.</para>
///
/// <para>Requests are numbered upwards from one and a hand is zero, as in <see cref="MapRequests"/>, so
/// a rest reported for a request older than the latest that set the tracker moving is from before that
/// move, which is still to come.</para>
/// </summary>
public sealed class MapTrackerRest
{
    /// <summary>The latest request that set the tracker moving, zero for a hand, or null while it is at rest.</summary>
    private long? _moving;

    /// <summary>Whether the tracker is at rest, with nothing on its way that sets it moving.</summary>
    public bool AtRest => _moving is null;

    /// <summary>The map asked for request <paramref name="id"/>, which sets the tracker moving by itself: a glide or a fling.</summary>
    public void Launched(long id) => _moving = id;

    /// <summary>A hand took the tracker, which moves until the hand's coast comes to rest.</summary>
    public void Taken() => _moving = 0;

    /// <summary>
    /// The tracker refused request <paramref name="id"/>, which it does only while a hand holds it: a
    /// move it refused never sets it moving, and the hand's rest is the one to wait for.
    /// </summary>
    public void Refused(long id)
    {
        if (_moving == id)
        {
            Taken();
        }
    }

    /// <summary>
    /// The tracker came to rest, as request <paramref name="id"/> left it. Says whether it is at rest
    /// now: a rest from before the latest move that set it moving says nothing of that move.
    /// </summary>
    public bool Rested(long id)
    {
        if (_moving is { } moving && id < moving)
        {
            return false;
        }

        _moving = null;
        return true;
    }
}
