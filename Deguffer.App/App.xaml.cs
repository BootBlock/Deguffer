using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;

namespace Deguffer.App;

public partial class App : Application
{
    private static MainWindow? _shell;

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

    /// <summary>
    /// The approved source folders, shared for the same reason <see cref="Preferences"/> is: a
    /// second store per page would let the Settings list and the folder actually scanned drift apart.
    /// </summary>
    public static SourceRootService SourceRoots { get; } =
        new(new SourceRootStore(UserEnvironment.Current));

    /// <summary>
    /// What the Storage rows were left ticked as, shared for the same reason the two above are: the
    /// page reads it as it builds each row and writes it back as the user clicks, so a second
    /// instance would hand out a copy that is stale by the first tick.
    /// </summary>
    public static SelectionService Selections { get; } =
        new(new SelectionStore(UserEnvironment.Current));

    /// <summary>
    /// The shell window, for the Win32 interop a folder picker needs — a <see cref="Page"/> has no
    /// route to its own window, and a picker without an owner handle throws rather than opening.
    /// </summary>
    public static Window? MainWindow => _shell;

    /// <summary>
    /// Get the window's placement onto disk now rather than at the close that follows.
    ///
    /// <see cref="ElevatedRelaunch"/> calls this because the replacement process reads that file as
    /// it opens, and a write that waits for this instance to close is one the replacement may open
    /// too early to see.
    /// </summary>
    internal static void RememberWindowPlacement() => _shell?.RememberPlacement();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _shell = new MainWindow();

        // Closing the only window ends the session rather than leaving the process resident. This
        // is explicit because a dialog dismissed at the wrong moment has been enough to leave a
        // WinUI message loop running with no UI attached to it.
        _shell.Closed += (_, _) => Exit();

        _shell.Activate();
    }
}
