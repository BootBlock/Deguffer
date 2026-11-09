using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Views;

/// <summary>
/// The map appearance window: presets, the colours each picture is drawn in, how every shape is
/// shaded and outlined, the light under the pointer, and the room a treemap leaves round each folder.
/// It changes nothing but how the maps look.
///
/// <para>Modeless and owned by the main window, so the reader can keep it open, move it clear of the
/// map, and watch each change land. It opens where it was left, and with the application where it was
/// left open. See <see cref="ToolWindowPlacement"/> and <see cref="ToolWindowOpener"/>.</para>
///
/// <para>Built in code around <see cref="MapAppearanceView"/>, with no markup of its own. The window
/// and its <see cref="AppWindow"/> are native objects that hold each handler on their events with a
/// plain COM reference, which the collector treats as a root and which WinUI's reference tracking does
/// not see, so a handler that reached the window kept it, its preview and its map alive for the life
/// of the process (#737). The compiled bindings of a window's markup subscribe to its
/// <see cref="Window.Activated"/> and never let go, so there are none here, and every handler this
/// window puts on its own events is taken off as it closes.</para>
/// </summary>
public sealed class MapAppearanceWindow : Window, IToolWindow
{
    private const int OpenWidth = 560;
    private const int OpenHeight = 860;

    /// <summary>One preset card wide, with every row still able to fit its box beside its words.</summary>
    private const int MinimumWidth = 440;

    /// <summary>The preview, the sections and the foot, with a few rows of the open section between them.</summary>
    private const int MinimumHeight = 560;

    private readonly MapAppearanceView _view;
    private readonly FrameworkElement? _ownerRoot;
    private readonly ToolWindowPlacement _placement;
    private readonly WindowBackdrop _backdrop;

    public MapAppearanceWindow(MapAppearanceViewModel viewModel, Window owner, WindowPlacementStore<ToolWindowMetrics> store)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _view = new MapAppearanceView(viewModel);
        Content = _view;

        Title = "Map appearance";
        WindowIcon.Apply(this);

        // §6.5: the content runs into the title bar so the material runs the full height, as the main
        // window's does.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_view.TitleBar);

        // A tool window: it is resized and moved but never maximised or minimised on its own, because
        // it goes and comes back with the window that owns it.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        _placement = new ToolWindowPlacement(this, owner, store, OpenWidth, OpenHeight, MinimumWidth, MinimumHeight);
        _placement.Apply();

        // The theme follows the main window's content rather than being worked out again from the
        // preference, so this is in whichever theme the reader is actually looking at — including a
        // system theme that changes while it is open. Before the backdrop, which follows the theme.
        _ownerRoot = owner.Content as FrameworkElement;

        if (_ownerRoot is not null)
        {
            FollowTheme(_ownerRoot, null);
            _ownerRoot.ActualThemeChanged += FollowTheme;
        }

        _backdrop = new WindowBackdrop(this, _view.Ground, SystemMotion.Current);
        _backdrop.IsRequested = App.Preferences.Current.BackdropEnabled;
        App.Preferences.Changed += OnPreferencesChanged;

        _view.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
    }

    /// <summary>Write where the window is to disk, and whether it is to open again with the application.</summary>
    public void Remember(bool isOpen) => _placement.Remember(isOpen);

    /// <summary>
    /// Put the window on screen without moving the focus to it. See <see cref="ToolWindowOpener.Reopen"/>.
    /// The view's bindings start as it loads, which needs no activation.
    /// </summary>
    public void ShowWithoutFocus() => AppWindow.Show(activateWindow: false);

    /// <summary>
    /// Take off every handler that reaches this window from anything that outlives it: the owner's
    /// theme, the preferences and the view-model belong to the application, and the window's own
    /// events are held by the native window, this handler among them.
    /// </summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_ownerRoot is not null)
        {
            _ownerRoot.ActualThemeChanged -= FollowTheme;
        }

        App.Preferences.Changed -= OnPreferencesChanged;
        _view.CloseRequested -= OnCloseRequested;
        _view.Detach();
        _placement.Detach();
        Closed -= OnClosed;
    }

    private void FollowTheme(FrameworkElement sender, object? args) => _view.RequestedTheme = sender.ActualTheme;

    /// <summary>The translucent background was switched on or off on the Settings page, which this window follows as the main one does.</summary>
    private void OnPreferencesChanged(object? sender, EventArgs e) =>
        _backdrop.IsRequested = App.Preferences.Current.BackdropEnabled;

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
