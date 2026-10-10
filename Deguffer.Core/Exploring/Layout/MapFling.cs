namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// How fast a hand dragging the picture with the mouse was moving when it let go, so the picture can
/// carry on at that speed and slow to a stop, as it does after a flick on a touch screen.
///
/// <para>Measured over the last <see cref="Window"/> of the drag rather than from the last two moves,
/// because a mouse reports at its own rate and two reports a millisecond apart make a speed out of
/// nothing. A hand that stopped before it let go flings nothing: the picture stays where it was put.</para>
///
/// <para>Positions are in device-independent pixels of the screen, and the speed in pixels a second,
/// which is what the tracker takes.</para>
/// </summary>
public sealed class MapFling
{
    /// <summary>How far back the speed is measured over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long a hand can be still before it lets go and still fling. Longer than the gap between two
    /// reports of a moving mouse, shorter than a hand that has deliberately stopped.
    /// </summary>
    public static readonly TimeSpan Rest = TimeSpan.FromMilliseconds(50);

    private readonly Queue<(double X, double Y, TimeSpan At)> _moves = new();

    /// <summary>Forget the last drag, for a new press.</summary>
    public void Clear() => _moves.Clear();

    /// <summary>The hand was at (<paramref name="x"/>, <paramref name="y"/>) at <paramref name="at"/>.</summary>
    public void Track(double x, double y, TimeSpan at)
    {
        _moves.Enqueue((x, y, at));

        while (_moves.Count > 2 && at - _moves.Peek().At > Window)
        {
            _moves.Dequeue();
        }
    }

    /// <summary>
    /// The hand let go at <paramref name="at"/>. Says how fast it was moving, in pixels a second,
    /// and zero where it was still.
    /// </summary>
    public (double X, double Y) Release(TimeSpan at)
    {
        if (_moves.Count < 2)
        {
            return (0, 0);
        }

        var first = _moves.Peek();
        var last = _moves.Last();
        var span = (last.At - first.At).TotalSeconds;

        if (at - last.At > Rest || span <= 0)
        {
            return (0, 0);
        }

        return ((last.X - first.X) / span, (last.Y - first.Y) / span);
    }
}
