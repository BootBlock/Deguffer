namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Where a <see cref="CanvasRedraw"/> puts its picture. Every call is made on the thread that
/// started the redraw, in this order: <see cref="Begin"/> once, <see cref="Land"/> any number of
/// times, then <see cref="Arrive"/> once. A redraw superseded after it began gets
/// <see cref="Withdraw"/> instead of the rest, and a redraw superseded before it began gets nothing.
/// </summary>
public interface ICanvasRedrawTarget
{
    /// <summary>
    /// <paramref name="redraw"/> is laid out, and its regions are about to land. Nothing of it is on
    /// screen yet, so whatever is there now goes on showing until a region covers it.
    /// </summary>
    void Begin(CanvasRedraw redraw);

    /// <summary>
    /// Put <paramref name="regions"/> of <paramref name="redraw"/>'s
    /// <see cref="CanvasRedraw.Pixels"/> on screen. Each is painted in full and is never handed over
    /// twice.
    /// </summary>
    void Land(CanvasRedraw redraw, IReadOnlyList<CanvasRegion> regions);

    /// <summary>Every region of <paramref name="redraw"/> is on screen, so its drawing is the picture now.</summary>
    void Arrive(CanvasRedraw redraw);

    /// <summary>
    /// <paramref name="redraw"/> was superseded before it arrived. Whatever of it landed comes off the
    /// screen, so the picture is the last one that arrived.
    /// </summary>
    void Withdraw(CanvasRedraw redraw);
}
