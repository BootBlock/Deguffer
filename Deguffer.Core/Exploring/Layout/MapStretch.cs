namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Where a hand dragging the picture puts it: under the hand while the screen stays inside the
/// picture, and past the edge only against a resistance that grows the further it is pulled, as a
/// sheet of rubber gives.
///
/// <para>The resistance says there is no more picture without stopping the hand dead, and the picture
/// springs back once it is let go. It never goes further past an edge than <see cref="Reach"/> of the
/// screen, however far the hand goes, so the picture never drifts out of view. Where the picture may
/// not stretch, for a reader who has turned animation effects off, the edge is a wall.</para>
///
/// <para>Held from the hand's own position rather than by adding up each step, so a hand that goes
/// past the edge and comes back puts the picture back exactly where it was under it.</para>
/// </summary>
public sealed class MapStretch
{
    /// <summary>How far past an edge the picture can be pulled at most, as a fraction of the screen.</summary>
    public const double Reach = 0.15;

    private readonly double _width;

    private readonly double _height;

    /// <summary>The tracker's position at each edge of the picture, at the drag's scale.</summary>
    private (double Least, double Most) _across;

    private (double Least, double Most) _down;

    private MapOrigin _origin;

    private readonly bool _elastic;

    /// <summary>Where the tracker would be if nothing held the picture back: where the hand has taken it.</summary>
    private double _handX;

    private double _handY;

    /// <summary>
    /// A drag starting from <paramref name="start"/> on a screen <paramref name="width"/> by
    /// <paramref name="height"/>, which goes past the picture's edge only where
    /// <paramref name="elastic"/>.
    /// </summary>
    public MapStretch(MapTracking start, double width, double height, bool elastic)
    {
        _width = width;
        _height = height;
        _across = start.Origin.Across(start.Scale, width);
        _down = start.Origin.Down(start.Scale, height);
        _origin = start.Origin;
        _elastic = elastic;
        _handX = start.X;
        _handY = start.Y;
        Scale = start.Scale;
    }

    /// <summary>The scale the drag is at, which a drag does not change.</summary>
    public double Scale { get; }

    /// <summary>
    /// The hand moved by (<paramref name="x"/>, <paramref name="y"/>) pixels of the screen. Says where
    /// the tracker goes: the picture moves with the hand, so the screen moves the other way.
    /// </summary>
    public MapTracking Pull(double x, double y)
    {
        _handX -= x;
        _handY -= y;

        return new MapTracking(
            Hold(_handX, _across, Reach * _width),
            Hold(_handY, _down, Reach * _height),
            Scale,
            _origin);
    }

    /// <summary>
    /// Measure the drag from <paramref name="origin"/> from now on, for a tracker that turned out to
    /// be measured from it: where the hand has the picture stays where it is.
    /// </summary>
    public void Remeasure(MapOrigin origin)
    {
        _handX += (_origin.Left - origin.Left) * Scale * _width;
        _handY += (_origin.Top - origin.Top) * Scale * _height;
        _origin = origin;
        _across = origin.Across(Scale, _width);
        _down = origin.Down(Scale, _height);
    }

    private double Hold(double asked, (double Least, double Most) edges, double reach)
    {
        if (!_elastic)
        {
            return Math.Clamp(asked, edges.Least, edges.Most);
        }

        if (asked < edges.Least)
        {
            return edges.Least - Give(edges.Least - asked, reach);
        }

        return asked > edges.Most ? edges.Most + Give(asked - edges.Most, reach) : asked;
    }

    /// <summary>
    /// How far past the edge a hand <paramref name="over"/> past it takes the picture: as far as the
    /// hand at first, and less for each step further, never reaching <paramref name="reach"/>.
    /// </summary>
    private static double Give(double over, double reach) => reach * over / (over + reach);
}
