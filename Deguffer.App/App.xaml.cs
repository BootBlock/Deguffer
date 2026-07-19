using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;

namespace Deguffer.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // Before InitializeComponent: XAML parsing runs framework callbacks of its own, and a
        // fault there is exactly the one with no other record.
        FaultReporting.Attach(this, Faults);

        InitializeComponent();
    }

    /// <summary>Where an unhandled exception is recorded before the process ends.</summary>
    public static CrashLog Faults { get; } = new(UserEnvironment.Current);

    /// <summary>
    /// Settings, read once at startup and shared by the window and every page. There is no
    /// container in this app and one type does not justify introducing one, but the pages do have
    /// to reach the same instance — constructing a second <see cref="PreferenceStore"/> per page
    /// would hand each one a copy that goes stale the moment anything changes.
    /// </summary>
    public static PreferenceService Preferences { get; } =
        new(new PreferenceStore(UserEnvironment.Current));

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();

        // Closing the only window ends the session rather than leaving the process resident. This
        // is explicit because a dialog dismissed at the wrong moment has been enough to leave a
        // WinUI message loop running with no UI attached to it.
        _window.Closed += (_, _) => Exit();

        _window.Activate();
    }
}
