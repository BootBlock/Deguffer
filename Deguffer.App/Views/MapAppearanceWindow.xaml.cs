using System.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The map appearance window: presets, the colours each picture is drawn in, how every shape is
/// shaded and outlined, the light under the pointer, and the room a treemap leaves round each folder.
/// It changes nothing but how the maps look.
///
/// <para>Modeless and owned by the main window, so the reader can keep it open, move it clear of the
/// map, and watch each change land. It opens where it was left, and with the application where it was
/// left open. See <see cref="ToolWindowPlacement"/> and <see cref="MapAppearanceOpener"/>.</para>
/// </summary>
public sealed partial class MapAppearanceWindow : Window
{
    private const int OpenWidth = 560;
    private const int OpenHeight = 860;

    /// <summary>One preset card wide, with every row still able to fit its box beside its words.</summary>
    private const int MinimumWidth = 440;

    /// <summary>The preview, the sections and the foot, with a few rows of the open section between them.</summary>
    private const int MinimumHeight = 560;

    private readonly FrameworkElement? _ownerRoot;
    private readonly ToolWindowPlacement _placement;
    private readonly WindowBackdrop _backdrop;

    public MapAppearanceWindow(MapAppearanceViewModel viewModel, Window owner, WindowPlacementStore<ToolWindowMetrics> store)
    {
        ArgumentNullException.ThrowIfNull(owner);

        ViewModel = viewModel;

        InitializeComponent();

        Title = "Map appearance";
        WindowIcon.Apply(this);

        // §6.5: the content runs into the title bar so the material runs the full height, as the main
        // window's does.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

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

        _backdrop = new WindowBackdrop(this, Ground, SystemMotion.Current);
        _backdrop.IsRequested = App.Preferences.Current.BackdropEnabled;
        App.Preferences.Changed += OnPreferencesChanged;

        ShowPreview();
        ViewModel.Changed += OnLookChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Closed += (_, _) =>
        {
            if (_ownerRoot is not null)
            {
                _ownerRoot.ActualThemeChanged -= FollowTheme;
            }

            // The view-model and the preferences belong to the application and outlive this window.
            App.Preferences.Changed -= OnPreferencesChanged;
            ViewModel.Changed -= OnLookChanged;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    public MapAppearanceViewModel ViewModel { get; }

    /// <summary>Write where the window is to disk, and whether it is to open again with the application.</summary>
    public void Remember(bool isOpen) => _placement.Remember(isOpen);

    /// <summary>Put the window on screen without moving the focus to it. See <see cref="MapAppearanceOpener.Reopen"/>.</summary>
    public void ShowWithoutFocus() => AppWindow.Show(activateWindow: false);

    private void FollowTheme(FrameworkElement sender, object? args) => Root.RequestedTheme = sender.ActualTheme;

    /// <summary>The translucent background was switched on or off on the Settings page, which this window follows as the main one does.</summary>
    private void OnPreferencesChanged(object? sender, EventArgs e) =>
        _backdrop.IsRequested = App.Preferences.Current.BackdropEnabled;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnRestoreClicked(object sender, RoutedEventArgs e) => ViewModel.RestoreDefaults();

    /// <summary>
    /// Show the section the reader chose and only that one, scrolled to its top: a section opened part
    /// way down, at the place the last one was left, starts the reader in the middle of it.
    /// </summary>
    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var chosen = sender.SelectedItem;

        PresetsSection.Visibility = Shown(chosen == PresetsItem);
        ColoursSection.Visibility = Shown(chosen == ColoursItem);
        ShapesSection.Visibility = Shown(chosen == ShapesItem);
        LinesSection.Visibility = Shown(chosen == LinesItem);
        LayoutSection.Visibility = Shown(chosen == LayoutItem);

        SectionScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static Visibility Shown(bool chosen) => chosen ? Visibility.Visible : Visibility.Collapsed;

    private void OnLookChanged(object? sender, EventArgs e) => ShowPreview();

    /// <summary>
    /// The page's View or Colour box moved, or a preset chose a colouring, so the scheme list and the
    /// preview speak for another picture, or the preview is coloured another way.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapAppearanceViewModel.View) or nameof(MapAppearanceViewModel.Colouring))
        {
            ShowPreview();
        }
    }

    private void ShowPreview() => Preview.Show(ViewModel.Look, ViewModel.View, ViewModel.Colouring);
}
