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
    /// A new picture coming in over the old one or giving way to it: the map's camera flying into a
    /// folder's shape, or pulling back out of it to the folder above, on a spring that settles in this
    /// long (see <see cref="Spring"/>). With motion off it fades in place rather than moving, because
    /// the whole of the screen changes, and a cut would leave the reader no hint that the new picture
    /// is the inside of the one they opened, or the outside of the one they left. Shorter than the move
    /// it stands in for, as it has no distance to cover.
    /// </summary>
    public static MotionToken Entrance { get; } = new(
        new Motion(Glide, Travels: true),
        new Motion(TimeSpan.FromMilliseconds(150), Travels: false));

    /// <summary>
    /// The overview a zoomed map shows in a corner, coming in as the map is zoomed in, going as it
    /// shows the whole picture again, and coming in at the other corner when the map is acted on
    /// beside it. With motion on it drops a short way into place as it fades in. With motion off it
    /// fades in place, so the reader still sees where it went.
    /// </summary>
    public static MotionToken Overview { get; } = new(
        new Motion(TimeSpan.FromMilliseconds(200), Travels: true),
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

    /// <summary>
    /// A bound list changing under the reader: a row arriving fades and slides in, one leaving fades
    /// out as the rows below glide into its space, and one the sort moved glides to its new place
    /// (see <see cref="ListMotion"/>). With motion off nothing glides or slides, because the rows
    /// would only travel a row's height or two and land where a jump puts them, but a row still fades
    /// in or out, so the reader can see that it came or went.
    /// </summary>
    public static MotionToken List { get; } = new(
        new Motion(TimeSpan.FromMilliseconds(200), Travels: true),
        new Motion(TimeSpan.FromMilliseconds(150), Travels: false));

    /// <summary>
    /// A bar following its figure: a row's share growing in as the row arrives and easing as a scan
    /// reports larger rows, or the used space on the disk draining as a clean gives it back. Each move
    /// goes from where the bar is drawn to the figure just reported, and never past it. With motion off
    /// the bar is at its figure at once, because the figure beside it already says the new value, and a
    /// bar that only arrives there later says nothing more.
    /// </summary>
    public static MotionToken Bar { get; } = new(
        new Motion(TimeSpan.FromMilliseconds(300), Travels: true),
        Motion.Instant);

    /// <summary>
    /// The figures a finished clean leaves on the page coming in: they rise a short way into place as
    /// they fade in, and nothing else marks the moment. They come in alike whatever the run found, so
    /// a protected path that did not survive arrives with the same weight as a clean that went to plan
    /// (§5.6). With motion off they fade in place, so the reader still sees that the run has finished.
    /// </summary>
    public static MotionToken Outcome { get; } = new(
        new Motion(TimeSpan.FromMilliseconds(250), Travels: true),
        new Motion(TimeSpan.FromMilliseconds(150), Travels: false));

    /// <summary>
    /// The About page's starfield, flown through on a loop this long (see <see cref="StarFlight"/>).
    /// With motion off it is a still field, drawn once: a loop that only faded would be a field that
    /// flickers for ever, and a reader who turned animation off has asked for neither.
    /// </summary>
    public static MotionToken Starfield { get; } = new(
        new Motion(TimeSpan.FromSeconds(40), Travels: true),
        Motion.Instant);

    /// <summary>
    /// The Deguffer mark flying in out of the About page's starfield and settling into the page's header,
    /// the first time the page is visited. Slower than any other move here, because it is the one move
    /// that is there to be watched. With motion off it fades in where it settles, so the header still
    /// comes in as every page's does.
    /// </summary>
    public static MotionToken Mark { get; } = new(
        new Motion(TimeSpan.FromMilliseconds(900), Travels: true),
        new Motion(TimeSpan.FromMilliseconds(150), Travels: false));

    /// <summary>How this plays, given whether the reader has animation effects on.</summary>
    public Motion For(bool animationsEnabled) => animationsEnabled ? Full : Reduced;
}
