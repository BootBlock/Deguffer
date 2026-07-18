using Microsoft.UI.Xaml;

namespace Deguffer.App;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();

        // Secondary windows (About) keep the message loop alive on their own, which would leave
        // the process running with the main UI gone. Closing the main window ends the session.
        _window.Closed += (_, _) => Exit();

        _window.Activate();
    }
}
