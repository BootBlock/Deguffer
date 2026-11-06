namespace Deguffer.App.Controls;

/// <summary>Something to do once, at the next frame a window composes.</summary>
internal static class NextFrame
{
    /// <summary>
    /// Call <paramref name="then"/> at the next frame of <paramref name="clock"/>, and stop listening
    /// to its frames before calling it, so nothing is paid for the frames after.
    /// </summary>
    public static void Then(IFrameClock clock, Action then)
    {
        EventHandler<object>? once = null;

        once = (_, _) =>
        {
            clock.Frame -= once;
            then();
        };

        clock.Frame += once;
    }
}
