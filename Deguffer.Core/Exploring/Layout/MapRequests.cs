namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Which of the interaction tracker's reports say where the map's camera is now, and where it has
/// come to rest.
///
/// <para>The tracker runs on the compositor and answers a while after it is asked, so a report can
/// arrive about a move the map has already replaced: a glide still reporting after the page reset the
/// camera for another picture would put the old camera back. Each request the map makes has a number,
/// each report carries the number of the request that caused it, and a hand on the camera (a touch, a
/// pinch, a touchpad or the wheel) is number zero. Only the latest is believed.</para>
///
/// <para>A request that goes somewhere the map chose comes to rest exactly there. The tracker keeps
/// its values in single precision, so its last report is a rounding error away, and the map keeps a
/// drawing of each place it stopped and shows it again only for exactly the same place.</para>
/// </summary>
public sealed class MapRequests
{
    /// <summary>How close, as a fraction of the screen or of the zoom, a report has to be to count as having arrived.</summary>
    private const double Arrived = 1e-5;

    /// <summary>The request the camera is following, or zero for a hand.</summary>
    private long _latest;

    /// <summary>Where that request goes, or null where the tracker decides where it stops, as a fling and a hand do.</summary>
    private MapViewport? _target;

    /// <summary>The map asked for request <paramref name="id"/>, which goes to <paramref name="target"/>, or to wherever the tracker stops it.</summary>
    public void Asked(long id, MapViewport? target)
    {
        _latest = id;
        _target = target;
    }

    /// <summary>A hand took the camera, and every request before it is over.</summary>
    public void Taken() => Asked(0, null);

    /// <summary>
    /// The tracker refused request <paramref name="id"/>, which it does while a hand holds the camera,
    /// so the hand's reports are the ones to follow.
    /// </summary>
    public void Refused(long id)
    {
        if (id == _latest)
        {
            Taken();
        }
    }

    /// <summary>Whether a report caused by request <paramref name="id"/> says where the camera is now.</summary>
    public bool Reports(long id) => id == _latest;

    /// <summary>
    /// What the screen shows, given that the tracker reported <paramref name="reported"/>: where the
    /// request was going, where it has got there, and otherwise the report as it is.
    /// </summary>
    public MapViewport Where(MapViewport reported) => At(reported) is { } target ? target : reported;

    /// <summary>
    /// Where the camera has come to rest, given that the tracker last reported
    /// <paramref name="reported"/>: where the request was going, if it got there, and otherwise the
    /// report held inside the picture.
    /// </summary>
    public MapViewport Rest(MapViewport reported) => At(reported) is { } target ? target : reported.Held();

    /// <summary>Where the request was going, where <paramref name="reported"/> is there, and otherwise null.</summary>
    private MapViewport? At(MapViewport reported) =>
        _target is { } target
        && Math.Abs((reported.Zoom / target.Zoom) - 1) < Arrived
        && Math.Abs(reported.Left - target.Left) < Arrived
        && Math.Abs(reported.Top - target.Top) < Arrived
            ? target
            : null;
}
