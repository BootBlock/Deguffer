using Deguffer.App.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Deguffer.App;

/// <summary>
/// The entry point, written here rather than generated from the XAML (the project defines
/// <c>DISABLE_XAML_GENERATED_MAIN</c>) so the launch a trace times starts before the application
/// object is made. Making it initialises <see cref="App"/>'s statics, which read the stores. See
/// <see cref="LaunchTiming"/>.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main() =>
        LaunchTiming.Run(ShellEvents.Log, LaunchTiming.SinceProcessStart, static () =>
        {
            // What the generated entry point does.
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(static _ =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App();
            });
        });
}
