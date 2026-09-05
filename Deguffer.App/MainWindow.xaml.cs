using Deguffer.App.Shell;
using Deguffer.App.Views;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
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
        _sizing = new WindowSizing(this, new WindowMetricsStore(UserEnvironment.Current));
        _sizing.Apply();

        // Where the window ends up is the user's, so it outlives the session that produced it.
        Closed += (_, _) => _sizing.Remember();

        ApplyPreferences();
        App.Preferences.Changed += (_, _) => ApplyPreferences();

        OpenWhereTheLaunchAsked();
    }

    /// <summary>
    /// Write the window's placement to disk without waiting for it to close. See
    /// <see cref="App.RememberWindowPlacement"/> for the one caller that needs that.
    /// </summary>
    public void RememberPlacement() => _sizing.Remember();

    /// <summary>
    /// Open on the destination this instance was started for: Storage ordinarily, and Explore where
    /// an elevated replacement was told to resume there.
    ///
    /// <para>An elevated instance always replaces one the user was already using, because §6.3 does
    /// not elevate at startup. Opening it at the default destination throws away where they were,
    /// and on Explore that includes a folder they chose through a dialog. See
    /// <see cref="ElevationRequest"/> for what survives the relaunch.</para>
    ///
    /// <para>Two measurements shape how the rail is moved, and neither reports an error when it is
    /// got wrong. Setting <c>IsSelected</c> on the item from here does not move the rail at all: it
    /// settles its selection later, and the window opened on Storage regardless. And a markup
    /// <c>IsSelected</c> stays true on its item after the rail has moved elsewhere, so the rail then
    /// reported both items selected. Hence the rail's own property, assigned here, and no starting
    /// selection in the markup.</para>
    /// </summary>
    private void OpenWhereTheLaunchAsked()
    {
        var explore = ElevatedRelaunch.Requested is ExploreRequest;

        ContentFrame.Navigate(explore ? typeof(ExplorePage) : typeof(CleanPage));

        // The rail second. Assigning it raises OnDestinationChanged, which finds the frame already
        // where it is going and does nothing further. The selection is what the user reads as "where
        // am I", and a frame showing Explore under a rail highlighting Storage is worse than either
        // of them alone.
        Navigation.SelectedItem = explore ? ExploreItem : StorageItem;
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
            "Explore" => typeof(ExplorePage),
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
