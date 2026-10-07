using Deguffer.Core.Viewing;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One move into a shape the reader opened: the shape grows from where it was on the screen until it
/// fills it, and the drawing of what is inside it grows with it, over the old picture, until it is all
/// that is left. Started at <paramref name="Start"/> on whatever clock the caller keeps, and played as
/// <paramref name="Motion"/> says, which is <see cref="MotionToken.Entrance"/>'s answer for the reader.
///
/// <para>Two drawings, because opening a folder draws it afresh across the whole canvas and that is
/// a different layout from the one it had inside its parent: its own shape, not its parent's slot.
/// The old picture is stretched so the shape fills the screen, which is what the reader asked for,
/// and the new one comes in over it on the same frame, so the one becomes the other rather than
/// cutting to it. Neither leaves any of the screen bare at any step, because the old picture is
/// stretched over all of it.</para>
///
/// <para>Stretched rather than magnified, and by more on one axis than the other where the shape is
/// not the screen's shape. It is a picture on its way to being replaced, it is on screen for a
/// quarter of a second, and it ends exactly where the new drawing begins.</para>
///
/// <para>A motion that does not travel grows nothing: both pictures stay where they are, filling the
/// screen, and the new one fades in over the old.</para>
/// </summary>
/// <param name="Shape">
/// Where the opened shape was on the screen, in fractions of it. Only the part on the screen is grown
/// from: a zoomed shape can run off the edges, and growing the whole of it would shrink the old
/// picture away from them.
/// </param>
public readonly record struct MapDescent(MapFrame Shape, TimeSpan Start, Motion Motion)
{
    /// <summary>Where the drawing of what was opened is on the screen at <paramref name="now"/>.</summary>
    public MapFrame Opened(TimeSpan now) =>
        Motion.Travels ? MapFrame.Between(OnScreen, MapFrame.Whole, Motion.At(Start, now)) : MapFrame.Whole;

    /// <summary>
    /// Where the old picture's screen is at <paramref name="now"/>: stretched so the opened shape lies
    /// exactly under the drawing of what is inside it. Carried by nothing where the motion does not
    /// travel: stretched at once to fill the screen, it would be the jump the motion is there to spare.
    /// </summary>
    public MapFrame Departing(TimeSpan now) => Motion.Travels ? OnScreen.Carried(Opened(now)) : MapFrame.Whole;

    private MapFrame OnScreen => Shape.Clipped(MapFrame.Whole);

    /// <summary>
    /// How opaque the drawing of what was opened is at <paramref name="now"/>, from nothing to all of
    /// it. The same easing as the move, so it is mostly there by the time it is mostly the screen.
    /// </summary>
    public double Opacity(TimeSpan now) => Motion.At(Start, now);

    /// <summary>Whether the move has arrived by <paramref name="now"/>.</summary>
    public bool IsOverAt(TimeSpan now) => Motion.IsOverAt(Start, now);
}
