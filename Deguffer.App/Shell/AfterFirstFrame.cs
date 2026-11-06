using Deguffer.App.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// Run something once the window's first frame has been drawn, rather than in the turn that frame is
/// waiting for.
///
/// <para><see cref="Microsoft.UI.Xaml.Window.Activate"/> only asks for a window to be shown: nothing is
/// laid out or rendered until the UI thread returns to its message loop. Anything built after it in the
/// same turn, a second window with its own XAML tree above all, puts the first frame back by the time it
/// takes to build.</para>
///
/// <para>The first frame tick is raised as that frame is about to be laid out, so it is the second tick
/// that comes after the first frame was handed to the compositor. Waiting on frames keeps them coming,
/// so the second follows the first by one refresh.</para>
/// </summary>
internal static class AfterFirstFrame
{
    /// <summary>Run <paramref name="action"/> once, at the frame after <paramref name="frames"/>' first.</summary>
    public static void Run(IFrameClock frames, Action action)
    {
        var seen = 0;

        void OnFrame(object? sender, object e)
        {
            if (++seen < 2)
            {
                return;
            }

            // Before the action, so a frame raised while it runs cannot run it again, and the frames
            // stop costing anything once it has run.
            frames.Frame -= OnFrame;
            action();
        }

        frames.Frame += OnFrame;
    }
}
