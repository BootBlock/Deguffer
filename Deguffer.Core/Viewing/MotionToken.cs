namespace Deguffer.Core.Viewing;

/// <summary>
/// One kind of animation, named for what it is for, and how it plays with Windows' Animation effects
/// on and with them off.
///
/// <para>Every animation asks for one of these by name rather than holding a duration of its own.
/// Composition animations do not follow the system setting on their own, as the framework's theme
/// transitions do, so an animation that carried its own timing would be one more place to forget it.</para>
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

    /// <summary>How this plays, given whether the reader has animation effects on.</summary>
    public Motion For(bool animationsEnabled) => animationsEnabled ? Full : Reduced;
}
