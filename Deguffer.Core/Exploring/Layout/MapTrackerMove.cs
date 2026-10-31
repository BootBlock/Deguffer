namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// How the interaction tracker is asked to move a map's camera from <paramref name="From"/> to
/// <paramref name="To"/>: to <paramref name="Position"/>, the tracker's position for the destination,
/// or by a scale about <paramref name="Pivot"/>, the point of the screen in pixels that stays still
/// throughout (<see cref="MapTracking.Pivot"/>).
///
/// <para>A scale about the still point carries the position with it, so a move with one is asked for
/// in one request, and a glide plays it as a scale through the key frames of <see cref="MapGlide"/>.
/// A move without one is a pan. A scale and a position asked for one after the other in the same
/// frame can leave the position wrong, which is a known fault of the tracker, so a jump asks for the
/// two together only where the zoom barely changes and there is no still point within reach
/// (<see cref="Rescales"/>).</para>
/// </summary>
public readonly record struct MapTrackerMove(MapViewport From, MapViewport To, MapTracking Position, (double X, double Y)? Pivot)
{
    /// <summary>
    /// The move from <paramref name="from"/> to <paramref name="to"/> on a screen
    /// <paramref name="width"/> by <paramref name="height"/>, with the tracker measured from
    /// <paramref name="origin"/>.
    /// </summary>
    public static MapTrackerMove Between(MapViewport from, MapViewport to, double width, double height, MapOrigin origin) => new(
        from,
        to,
        MapTracking.Of(to, width, height, origin),
        MapTracking.Pivot(from, to, width, height));

    /// <summary>
    /// Whether a jump without a still point has to change the scale before the position: the zoom
    /// changes, but too little to have a still point within <see cref="MapTracking.PivotReach"/>.
    /// </summary>
    public bool Rescales => Pivot is null && From.Zoom != To.Zoom;
}
