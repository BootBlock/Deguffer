namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// A press on a map's overview, and where it takes the camera (see <see cref="MapOverview"/>).
///
/// <para>A press on the rectangle round the screen drags it: the screen goes where the rectangle
/// goes. A press anywhere else in the overview flies the camera there once it is let go, or, once it
/// moves, puts the middle of the screen under the hand and keeps it there. A press becomes a drag as
/// one on the map does, past <see cref="MapDrag.Threshold"/>, so a hand that wanders a pixel still
/// clicks.</para>
///
/// <para>Measured from the camera as it was when the press went down, so a drag adds up to where the
/// hand is rather than to the steps it took. Positions are in the overview's device-independent
/// pixels.</para>
/// </summary>
public sealed class MapOverviewPress
{
    private readonly MapDrag _drag = new();

    /// <summary>Where the press went down, while it is held.</summary>
    private (double X, double Y)? _down;

    /// <summary>How far a drag has gone from where the press went down.</summary>
    private (double X, double Y) _moved;

    private (double Width, double Height) _size;

    private MapViewport _from;

    private double _ceiling;

    private bool _onFrame;

    /// <summary>Whether a press is held.</summary>
    public bool IsHeld => _down is not null;

    /// <summary>
    /// A press at (<paramref name="x"/>, <paramref name="y"/>) on an overview
    /// <paramref name="width"/> by <paramref name="height"/>, with the screen showing
    /// <paramref name="shown"/>, no deeper than <paramref name="ceiling"/>.
    /// </summary>
    public void Press(double x, double y, double width, double height, MapViewport shown, double ceiling)
    {
        _drag.Press(x, y, movable: true);
        _down = (x, y);
        _moved = (0, 0);
        _size = (width, height);
        _from = shown;
        _ceiling = ceiling;
        _onFrame = MapOverview.Frame(shown, width, height).Contains(x / width, y / height);
    }

    /// <summary>
    /// The pointer moved to (<paramref name="x"/>, <paramref name="y"/>), with the button still
    /// <paramref name="held"/> or not. Returns where the camera goes at once, or null where this move
    /// does not drag.
    /// </summary>
    public MapViewport? Move(double x, double y, bool held)
    {
        if (_drag.Move(x, y, held) is not (var stepX, var stepY))
        {
            if (!held)
            {
                _down = null;
            }

            return null;
        }

        _moved = (_moved.X + stepX, _moved.Y + stepY);

        return _onFrame
            ? MapOverview.Dragged(_from, _moved.X / _size.Width, _moved.Y / _size.Height)
            : MapOverview.Centred(_from, x / _size.Width, y / _size.Height, _ceiling);
    }

    /// <summary>
    /// The button came up, or the pointer was taken away. Returns where the camera flies to, which is
    /// where a click off the rectangle asked for, and null for a drag, a click on the rectangle, or no
    /// press at all.
    /// </summary>
    public MapViewport? Release()
    {
        var dragged = _drag.Release();

        if (_down is not { } down)
        {
            return null;
        }

        _down = null;

        return dragged || _onFrame
            ? null
            : MapOverview.Centred(_from, down.X / _size.Width, down.Y / _size.Height, _ceiling);
    }
}
