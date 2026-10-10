namespace Deguffer.Core.Viewing;

/// <summary>
/// One kind of animation, named for what it is for, and how it plays with Windows' Animation effects
/// on and with them off.
///
/// <para>Every animation asks for one of these by name rather than holding a duration of its own.
/// An animation the app clocks itself, or a composition animation, does not follow the system setting
/// on its own as the framework's theme transitions do, so one that carried its own timing would be one
/// more place to forget it.</para>
/// </summary>
/// <param name="Full">How it plays for a reader who has animation effects on.</param>
/// <param name="Reduced">How it plays for one who has turned them off.</param>
public sealed record MotionToken(Motion Full, Motion Reduced)
{
    /// <summary>
    /// Long enough to see where the picture went, short enough that a run of wheel turns does not
    /// queue up behind itself: each turn restarts the move from where it is.
    /// </summary>
    private static readonly TimeSpan Glide = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The camera over the map: a zoom, or a shape glided to fill the screen. With motion off it
    /// jumps, because the picture under the pointer is still the picture under the pointer when it
    /// arrives, and nothing is lost by not watching it travel.
    /// </summary>
    public static MotionToken Camera { get; } = new(new Motion(Glide, Travels: true), Motion.Instant);

    /// <summary>
    /// A new picture coming in over the old one: a folder opening out of its shape on the map. With
    /// motion off it fades in place rather than growing, because the whole of the screen changes, and
    /// a cut would leave the reader no hint that the new picture is the inside of the one they opened.
    /// Shorter than the move it stands in for, as it has no distance to cover.
    /// </summary>
    public static MotionToken Entrance { get; } = new(
        new Motion(Glide, Travels: true),
        new Motion(TimeSpan.FromMilliseconds(150), Travels: false));

    /// <summary>
    /// A page arriving from the navigation rail, rising a short way into place as it fades in, its
    /// header first (see <see cref="PageEntrance"/>). With motion off it fades in place, for the reason
    /// <see cref="Entrance"/> does: the whole page changes, and a fade says that it did.
    /// </summary>
    public static MotionToken Page { get; } = new(
        new Motion(TimeSpan.FromMilliseconds(200), Travels: true),
        new Motion(TimeSpan.FromMilliseconds(150), Travels: false));

    /// <summary>
    /// The whole window changing how it looks, for a theme or the backdrop turned on or off: the old
    /// look fades into the new. Nothing travels, so it plays with motion off as well, only shorter,
    /// because a window that repaints at once is a flash, which a reader who turned animation off has
    /// not asked for either.
    /// </summary>
    public static MotionToken Crossfade { get; } = new(
        new Motion(TimeSpan.FromMilliseconds(250), Travels: false),
        new Motion(TimeSpan.FromMilliseconds(150), Travels: false));

    /// <summary>
    /// Names arriving on a picture already on screen, with the detail a redraw brought: a zoom or a
    /// drag that stopped, or a folder opened. They fade in rather than appear, so the names a move
    /// uncovered come on as the picture settles rather than in a flash after it. Nothing travels, so
    /// it plays with motion off as well, only shorter, for the reason <see cref="Crossfade"/> does.
    /// </summary>
    public static MotionToken Detail { get; } = new(
        new Motion(TimeSpan.FromMilliseconds(200), Travels: false),
        new Motion(TimeSpan.FromMilliseconds(150), Travels: false));

    /// <summary>How this plays, given whether the reader has animation effects on.</summary>
    public Motion For(bool animationsEnabled) => animationsEnabled ? Full : Reduced;
}
