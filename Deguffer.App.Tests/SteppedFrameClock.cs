using Deguffer.App.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// A frame clock the test moves on by hand, one frame at a time, so an animation can be watched at
/// any point of its course without a window.
/// </summary>
internal sealed class SteppedFrameClock : IFrameClock
{
    public TimeSpan Now { get; private set; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether anything is waiting on frames, which an animation at rest must not be.</summary>
    public bool IsTicking => Frame is not null;

    public event EventHandler<object>? Frame;

    /// <summary>Move the time on by <paramref name="elapsed"/> and compose one frame there.</summary>
    public void Step(TimeSpan elapsed)
    {
        Now += elapsed;
        Frame?.Invoke(this, EventArgs.Empty);
    }
}
