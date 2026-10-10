namespace Deguffer.Core.Viewing;

/// <summary>
/// How a page arriving from the navigation rail comes in: its header first, and the rest of it a
/// step behind, each as <see cref="MotionToken.Page"/> plays.
///
/// <para>The whole of it is over in under 300 ms, so it never stands between the reader and the page.
/// It holds up no input either: it only fades and moves what is drawn, and a click lands where the
/// page is laid out, from the first frame.</para>
/// </summary>
public static class PageEntrance
{
    /// <summary>How far behind its header the rest of a page starts, as a share of the motion.</summary>
    public const double Lag = 0.25;

    /// <summary>
    /// When a part of a page starts coming in, after the entrance does, as <paramref name="motion"/>
    /// plays: its header at once, and the rest behind it.
    /// </summary>
    public static TimeSpan Start(bool isHeader, Motion motion) => isHeader ? TimeSpan.Zero : motion.Duration * Lag;
}
