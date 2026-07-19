using Deguffer.App.Shell;
using Deguffer.App.Views;
using Deguffer.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App;

public sealed partial class MainWindow : Window
{
    private readonly WindowBackdrop _backdrop;
    private readonly WindowSizing _sizing;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Deguffer";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        WindowIcon.Apply(this);

        _backdrop = new WindowBackdrop(this);
        _sizing = new WindowSizing(this);
        _sizing.Apply();

        ApplyPreferences();
        App.Preferences.Changed += (_, _) => ApplyPreferences();

        ContentFrame.Navigate(typeof(CleanPage));
    }

    /// <summary>
    /// Applying what the user chose is the window's job: the preference service holds the values,
    /// and turning a theme into an <see cref="ElementTheme"/> or a flag into a system backdrop is
    /// something only whoever owns the window can do.
    /// </summary>
    private void ApplyPreferences()
    {
        var preferences = App.Preferences.Current;

        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = preferences.Theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        _backdrop.IsRequested = preferences.BackdropEnabled;
    }

    private void OnDestinationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        var page = tag switch
        {
            "Settings" => typeof(SettingsPage),
            "About" => typeof(AboutPage),
            _ => typeof(CleanPage),
        };

        // Navigating to the page already shown would rebuild it, and CleanPage holds a scan the
        // user may be part-way through acting on.
        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }
}
