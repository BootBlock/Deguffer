namespace Deguffer.Core.Viewing;

/// <summary>
/// How one animation plays: how long it takes, and whether anything on the screen travels or only
/// fades in place. Taken from a <see cref="MotionToken"/> rather than written where it is used, so
/// every animation of one kind moves alike and none of them can forget the reader's motion setting.
///
/// <para>Timed against whatever clock the caller keeps, by the time elapsed rather than by frames
/// counted, so a frame the display drops leaves the animation where it should be rather than behind.</para>
/// </summary>
/// <param name="Duration">How long it takes. Zero lands at its end at once.</param>
/// <param name="Travels">
/// Whether anything moves or grows. A motion that does not travel only fades, in place, which is what
/// is left of an entrance for a reader who has turned animation off.
/// </param>
public readonly record struct Motion(TimeSpan Duration, bool Travels)
{
    /// <summary>At its end at once, with nothing in between.</summary>
    public static Motion Instant { get; } = new(TimeSpan.Zero, Travels: false);

    /// <summary>Whether this lands at its end the moment it starts, so there is nothing to clock.</summary>
    public bool IsInstant => Duration <= TimeSpan.Zero;

    /// <summary>
    /// How far through, from 0 to 1 and eased, an animation started at <paramref name="start"/> is
    /// at <paramref name="now"/>.
    /// </summary>
    public double At(TimeSpan start, TimeSpan now) =>
        IsInstant ? 1 : Eased(Math.Clamp((now - start) / Duration, 0, 1));

    /// <summary>Whether an animation started at <paramref name="start"/> has arrived by <paramref name="now"/>.</summary>
    public bool IsOverAt(TimeSpan start, TimeSpan now) => IsInstant || now - start >= Duration;

    /// <summary>
    /// A cubic ease out: fastest at the start, settling into place. A move that starts at speed is
    /// the answer to the input that asked for it; one that eased in would lag behind the hand.
    /// </summary>
    private static double Eased(double progress) => 1 - Math.Pow(1 - progress, 3);
}
