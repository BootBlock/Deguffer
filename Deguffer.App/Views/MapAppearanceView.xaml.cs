using System.ComponentModel;
using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// What the map appearance window shows: presets, the colours each picture is drawn in, how every
/// shape is shaded and outlined, the light under the pointer, and the room a treemap leaves round each
/// folder, above a preview of the look they make. See <see cref="MapAppearanceWindow"/>, which holds it.
/// </summary>
public sealed partial class MapAppearanceView : UserControl
{
    public MapAppearanceView(MapAppearanceViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();

        ShowPreview();
        ViewModel.Changed += OnLookChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>The reader pressed Close.</summary>
    public event EventHandler? CloseRequested;

    public MapAppearanceViewModel ViewModel { get; }

    /// <summary>
    /// Stop following the view-model, which belongs to the application and outlives this view, for a
    /// window that has closed.
    /// </summary>
    public void Detach()
    {
        ViewModel.Changed -= OnLookChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

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
