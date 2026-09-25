namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One eased move of a zoom from where it is to where it was asked to go, started at
/// <paramref name="Start"/> on whatever clock the caller keeps.
///
/// <para>A value started afresh for each turn of the wheel rather than a running animation with
/// state, so a zoom asked for again mid-move begins from wherever the screen is at that moment and
/// never jumps. Timed by the caller's clock rather than by counting frames, so a frame the display
/// drops leaves the zoom where it should be rather than behind.</para>
/// </summary>
public readonly record struct MapGlide(MapViewport From, MapViewport To, TimeSpan Start)
{
    /// <summary>
    /// How long a move takes. Long enough to see where the picture went, short enough that a run of
    /// wheel turns does not queue up behind itself: each turn restarts the move from where it is.
    /// </summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(250);

    /// <summary>Where the move is at <paramref name="now"/>.</summary>
    public MapViewport At(TimeSpan now) => MapViewport.Between(From, To, Eased(Progress(now)));

    /// <summary>Whether the move has arrived by <paramref name="now"/>.</summary>
    public bool IsOverAt(TimeSpan now) => Progress(now) >= 1;

    private double Progress(TimeSpan now) => Math.Clamp((now - Start) / Duration, 0, 1);

    /// <summary>
    /// A cubic ease out: fastest at the start, settling into place. A move that starts at speed is
    /// the answer to the wheel turn that asked for it; one that eased in would lag behind the hand.
    /// <see cref="MapDescent"/> moves on the same curve, so every move the map makes feels alike.
    /// </summary>
    internal static double Eased(double progress) => 1 - Math.Pow(1 - progress, 3);
}
