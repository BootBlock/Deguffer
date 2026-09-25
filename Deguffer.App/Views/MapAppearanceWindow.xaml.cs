using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Views;

/// <summary>
/// The map appearance window: the colours each picture is drawn in, and the room a treemap leaves
/// round each folder. It changes nothing but how the maps look.
///
/// <para>Modeless and owned by the main window, so the reader can keep it open, move it clear of
/// the map, and watch each change land. See <see cref="ToolWindowPlacement"/>.</para>
/// </summary>
public sealed partial class MapAppearanceWindow : Window
{
    private const int OpenWidth = 380;
    private const int OpenHeight = 620;

    private readonly FrameworkElement? _ownerRoot;

    public MapAppearanceWindow(MapAppearanceViewModel viewModel, Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        ViewModel = viewModel;

        InitializeComponent();

        Title = "Map appearance";
        WindowIcon.Apply(this);

        // A tool window: it is resized and moved but never maximised or minimised on its own, because
        // it goes and comes back with the window that owns it.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        ToolWindowPlacement.Place(this, owner, OpenWidth, OpenHeight);

        // The theme follows the main window's content rather than being worked out again from the
        // preference, so this is in whichever theme the reader is actually looking at — including a
        // system theme that changes while it is open.
        _ownerRoot = owner.Content as FrameworkElement;

        if (_ownerRoot is not null)
        {
            FollowTheme(_ownerRoot, null);
            _ownerRoot.ActualThemeChanged += FollowTheme;
        }

        Closed += (_, _) =>
        {
            if (_ownerRoot is not null)
            {
                _ownerRoot.ActualThemeChanged -= FollowTheme;
            }
        };
    }

    public MapAppearanceViewModel ViewModel { get; }

    private void FollowTheme(FrameworkElement sender, object? args) => Root.RequestedTheme = sender.ActualTheme;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
