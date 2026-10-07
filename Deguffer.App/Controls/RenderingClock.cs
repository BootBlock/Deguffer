using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace Deguffer.App.Controls;

/// <summary>
/// The window's frames, from <see cref="CompositionTarget.Rendering"/>, and one clock for every
/// animation in the app, read at each frame rather than started per move (G5).
/// </summary>
internal sealed class RenderingClock : IFrameClock
{
    public static RenderingClock Current { get; } = new();

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private RenderingClock()
    {
    }

    public TimeSpan Now => _clock.Elapsed;

    public event EventHandler<object>? Frame
    {
        add => CompositionTarget.Rendering += value;
        remove => CompositionTarget.Rendering -= value;
    }
}
