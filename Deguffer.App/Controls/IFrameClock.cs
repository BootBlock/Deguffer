namespace Deguffer.App.Controls;

/// <summary>
/// The time now, and a tick at each frame the window composes, for an animation the app clocks
/// itself. A seam so the shell's tests can step an animation frame by frame without a window. See
/// <see cref="RenderingClock"/>.
/// </summary>
internal interface IFrameClock
{
    TimeSpan Now { get; }

    /// <summary>
    /// Raised at every frame while anything is subscribed, which is sixty or more times a second.
    /// Subscribe only while something is moving: an idle subscriber pays for every frame regardless.
    /// </summary>
    event EventHandler<object>? Frame;
}
