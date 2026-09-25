namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Whether a press on the map is a click or a drag of the picture, and how far a drag has moved.
///
/// <para>A press becomes a drag once it has moved <see cref="Threshold"/> from where it went down,
/// and from then on every move is a drag, however small. Until then it is a click, left for the
/// framework to raise, because a hand is never perfectly still and a click that wandered a pixel
/// still means the shape under it.</para>
///
/// <para>Only a picture that can move is dragged. The whole picture has nowhere to go, so a press on
/// it that wanders stays a click: armed there, it would show a drag that moved nothing and swallow
/// the click the reader meant, leaving the last pick in place for Delete to act on (§7.1).</para>
///
/// <para>In Core rather than in the shell, as <see cref="MapGlide"/> is, so the rules are provable
/// without a window (G8). Positions are in device-independent pixels, which is what the threshold is
/// stated in.</para>
/// </summary>
public sealed class MapDrag
{
    /// <summary>
    /// How far a press has to move before it drags rather than clicks: the size of the rectangle
    /// Windows itself allows a click to wander in before it calls it a drag.
    /// </summary>
    public const double Threshold = 4;

    /// <summary>Where the press went down, and then where the drag was last seen, while it is held.</summary>
    private (double X, double Y)? _held;

    /// <summary>Whether the press held now has become a drag.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>
    /// Whether the last press became a drag, so the click the framework may still raise when it is
    /// released picks nothing: the reader was moving the picture, not pointing at a shape in it.
    /// Cleared by the next press.
    /// </summary>
    public bool Dragged { get; private set; }

    /// <summary>A press at (<paramref name="x"/>, <paramref name="y"/>), which can drag only where <paramref name="movable"/>.</summary>
    public void Press(double x, double y, bool movable)
    {
        Dragged = false;
        IsDragging = false;
        _held = movable ? (x, y) : null;
    }

    /// <summary>
    /// The pointer moved to (<paramref name="x"/>, <paramref name="y"/>), with the button still
    /// <paramref name="held"/> or not. Returns how far to drag the picture, or null where this move
    /// does not drag it.
    /// </summary>
    public (double X, double Y)? Move(double x, double y, bool held)
    {
        if (_held is not { } from)
        {
            return null;
        }

        // Let go somewhere the map never heard about, before any drag had captured the pointer.
        if (!held)
        {
            Release();
            return null;
        }

        if (!IsDragging && Math.Abs(x - from.X) < Threshold && Math.Abs(y - from.Y) < Threshold)
        {
            return null;
        }

        IsDragging = true;
        Dragged = true;
        _held = (x, y);

        return (x - from.X, y - from.Y);
    }

    /// <summary>The button came up, or the pointer was taken away. Says whether a drag was on.</summary>
    public bool Release()
    {
        var dragging = IsDragging;

        _held = null;
        IsDragging = false;

        return dragging;
    }
}
