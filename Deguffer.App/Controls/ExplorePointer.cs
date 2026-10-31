using Deguffer.Core.Exploring.Rendering;
using Windows.Foundation;

namespace Deguffer.App.Controls;

/// <summary>
/// What the pointer is over on the map, and when that is said and marked out: where the pointer was last
/// seen, the place in the picture under it, and the readout and the outline that follow it.
///
/// <para>The map hands over how a point is found in what the screen shows (<see cref="MapHitTest"/>), so
/// every answer is of the picture on screen (§7.1), how the pointer's light is placed, how what the
/// pointer is over is outlined, and how it is reported. The drag, the wheel and the routed pointer
/// handlers stay in the map, which owns the pointer capture and the cursor.</para>
///
/// <para>This is the only thing that says what the pointer is over, so a report it skips because nothing
/// changed is never one the page needed: a page that also cleared the readout would blank its own line
/// and never hear otherwise, and the outline would then mark a shape nothing named.</para>
/// </summary>
internal sealed class ExplorePointer
{
    private readonly Func<Point, MapLocation?> _locate;
    private readonly Action<MapLocation?> _light;
    private readonly Action _outline;
    private readonly Action<ExploreHit?> _report;

    /// <param name="locate">Where a point, in the map's own coordinates, falls in what the screen shows there, or null where nothing answers.</param>
    /// <param name="light">Put the pointer's light at a place, or take it away for null.</param>
    /// <param name="outline">Outline what the pointer is over again, from <see cref="Over"/>.</param>
    /// <param name="report">Say what the pointer is over, or null for nothing.</param>
    public ExplorePointer(Func<Point, MapLocation?> locate, Action<MapLocation?> light, Action outline, Action<ExploreHit?> report)
    {
        _locate = locate;
        _light = light;
        _outline = outline;
        _report = report;
    }

    /// <summary>
    /// Where the pointer was last seen, in the map's own coordinates, or null while it is elsewhere. Kept
    /// so a redraw can say what is under it again: a page that refreshes on its own would otherwise drop
    /// the outline and the readout under a pointer that never moved.
    /// </summary>
    public Point? Position { get; private set; }

    /// <summary>
    /// Where the pointer is over the picture, or null where it is over nothing. Its tile, while the camera
    /// moves, is what the outline is drawn from, because the drawing worked from may not draw the shape.
    /// </summary>
    public MapLocation? Over { get; private set; }

    /// <summary>The pointer is at <paramref name="position"/>. Nothing is said until it is followed.</summary>
    public void MoveTo(Point position) => Position = position;

    /// <summary>
    /// Say what is under the pointer, and mark it out, where that is not what was already under it: once
    /// per move of the pointer or of the picture beneath it.
    ///
    /// <para>Only when it changed. A pointer moves at the display's refresh rate and lands on the same shape
    /// for most of that, so reporting every move would rebuild the same string sixty times a second. The
    /// same shape found on another tile, or on the drawing, is outlined again from there, and not reported
    /// again.</para>
    /// </summary>
    public void Follow()
    {
        if (Position is not { } position)
        {
            return;
        }

        var over = _locate(position);
        var earlier = Over;

        Over = over;
        _light(over);

        if (over?.Hit == earlier?.Hit && over?.Tile == earlier?.Tile)
        {
            return;
        }

        _outline();

        if (over?.Hit != earlier?.Hit)
        {
            _report(over?.Hit);
        }
    }

    /// <summary>
    /// Say what is under the pointer in a picture that has just changed under it, and mark it out whether or
    /// not it changed: a new drawing is new geometry, so the outline is somewhere else now even round the
    /// same shape.
    ///
    /// <para>Asked again rather than dropped, because a page that redraws on its own leaves the pointer where
    /// it was: a scan publishing a snapshot, or a memory view refreshing every couple of seconds. Nothing is
    /// reported while the answer has not changed, so a redraw that leaves the same shape under the pointer
    /// costs nothing.</para>
    /// </summary>
    public void Reread()
    {
        var over = Position is { } position ? _locate(position) : null;
        var earlier = Over;

        Over = over;
        _light(over);
        _outline();

        if (over?.Hit != earlier?.Hit)
        {
            _report(over?.Hit);
        }
    }

    /// <summary>The pointer left the map: it is over nothing, its light goes, and the readout says so.</summary>
    public void Leave()
    {
        Position = null;
        Over = null;

        _light(null);
        _outline();
        _report(null);
    }

    /// <summary>
    /// The picture the pointer was over has gone, with nothing in its place yet: it is over nothing until
    /// the next is read. The map says so itself where it does.
    /// </summary>
    public void Forget() => Over = null;
}
