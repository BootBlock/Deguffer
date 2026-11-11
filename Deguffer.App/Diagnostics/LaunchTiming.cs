using System.Diagnostics;

namespace Deguffer.App.Diagnostics;

/// <summary>
/// Where the <c>Launch</c> pair a trace times begins: at the process's entry point, before the
/// application starts, and measured back from there to the moment Windows created the process.
///
/// <para>It began in <see cref="App.OnLaunched"/> once, which missed everything before it: the
/// runtime's start, and <see cref="App"/>'s type initialiser, which reads the stores
/// (<see cref="Core.Configuration.PreferenceStore"/> among them) the first time the application
/// object touches a static. That part was 380 to 823 ms of a warm launch of about a second.</para>
/// </summary>
internal static class LaunchTiming
{
    /// <summary>
    /// Write the launch's start, then run <paramref name="application"/>, in that order, so the span
    /// holds everything the application does before its first frame.
    /// </summary>
    /// <param name="sinceProcessStart">
    /// How long ago Windows created this process. Asked only while a trace is recording, so a session
    /// with no trace does not open its own process to read it.
    /// </param>
    public static void Run(ShellEvents events, Func<TimeSpan> sinceProcessStart, Action application)
    {
        if (events.IsEnabled())
        {
            events.LaunchStart((int)Math.Round(sinceProcessStart().TotalMilliseconds));
        }

        application();
    }

    /// <summary>How long ago Windows created this process, from the creation time it records.</summary>
    public static TimeSpan SinceProcessStart()
    {
        using var self = Process.GetCurrentProcess();

        // StartTime is local time, so it is compared with the local clock.
        return DateTime.Now - self.StartTime;
    }
}
