namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Where the two pictures of a change of folder on the map are at each step of the move between them:
/// the outer picture, of the folder that holds the other, and the inner one, of the folder inside it.
/// Opening a folder goes from the outer picture filling the screen to the inner one filling it, and
/// going back up goes the other way along the same path, so both are one camera flying in or out.
///
/// <para>Two drawings, because a folder opened is drawn afresh across the whole screen, which is a
/// different layout from the one it had inside its parent: its own shape, not its parent's slot. The
/// outer picture is stretched so the folder's slot in it lies exactly under the inner picture at every
/// step, so the one becomes the other rather than cutting to it. Neither leaves any of the screen bare,
/// because the outer picture covers all of it throughout.</para>
///
/// <para>Stretched rather than magnified, by more on one axis than the other where the slot is not the
/// screen's shape. It is a picture on its way to being replaced, it is on screen for a quarter of a
/// second, and it ends exactly where the other drawing begins.</para>
///
/// <para>On each axis the outer picture is magnified about the one point of the screen that stays
/// where it is from end to end, by equal ratios rather than equal steps, as
/// <see cref="MapViewport.Between"/> zooms: a flight several folders deep spends as long on each
/// doubling, which reads as a steady speed rather than as a lurch at one end.</para>
///
/// <para>The clock is the caller's: this says where the two pictures are at a progress from 0, the
/// outer picture filling the screen, to 1, the inner one filling it.</para>
/// </summary>
/// <param name="Shape">
/// Where the inner picture's screen lies on the outer picture's screen, in fractions of it. Only the
/// part on the screen is flown to: a zoomed shape can run off the edges, and flying to the whole of it
/// would shrink the outer picture away from them.
/// </param>
/// <param name="Travels">
/// Whether anything moves. Where nothing does, both pictures fill the screen throughout, and the inner
/// one fades in over the outer one, or out of it.
/// </param>
public readonly record struct MapDescent(MapFrame Shape, bool Travels)
{
    /// <summary>
    /// Where the inner picture's screen is at <paramref name="progress"/>. Exactly the screen at the
    /// end, where the map's own camera takes the inner picture over, so the hand-over moves nothing
    /// by a rounding error.
    /// </summary>
    public MapFrame Inner(double progress) =>
        Flies && progress < 1 ? Outer(progress).Inside(OnScreen) : MapFrame.Whole;

    /// <summary>
    /// Where the outer picture's screen is at <paramref name="progress"/>: magnified on each axis so
    /// the folder's slot in it lies exactly under the inner picture.
    /// </summary>
    public MapFrame Outer(double progress)
    {
        if (!Flies)
        {
            return MapFrame.Whole;
        }

        progress = Math.Clamp(progress, 0, 1);

        var (x, width) = Axis(OnScreen.X, OnScreen.Width, progress);
        var (y, height) = Axis(OnScreen.Y, OnScreen.Height, progress);

        return new MapFrame(x, y, width, height);
    }

    /// <summary>
    /// How opaque the inner picture is at <paramref name="progress"/>: none of it with the outer
    /// picture filling the screen, and all of it once it fills the screen itself. In step with the
    /// move, so the inner picture is mostly there by the time it is mostly the screen.
    /// </summary>
    public double Opacity(double progress) => Math.Clamp(progress, 0, 1);

    private MapFrame OnScreen => Shape.Clipped(MapFrame.Whole);

    /// <summary>Whether the pictures move at all: not where the motion stays in place, nor for a slot with nothing of it on screen.</summary>
    private bool Flies => Travels && OnScreen is { Width: > 0, Height: > 0 };

    /// <summary>
    /// Where the outer picture's screen is on one axis at <paramref name="progress"/>, for a slot at
    /// <paramref name="start"/> spanning <paramref name="span"/> of it: magnified by
    /// (1 / span)^progress about the point that the slot's start and end both map to themselves.
    /// A slot that spans the whole axis leaves it as it is.
    /// </summary>
    private static (double Start, double Span) Axis(double start, double span, double progress)
    {
        if (span >= 1)
        {
            return (0, 1);
        }

        var magnified = Math.Pow(1 / span, progress);
        var still = start / (1 - span);

        return (still * (1 - magnified), magnified);
    }
}
