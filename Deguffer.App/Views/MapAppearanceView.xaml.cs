using System.ComponentModel;
using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// What the map appearance window shows: presets, the colours each picture is drawn in, how every
/// shape is shaded and outlined, the light under the pointer, the room a treemap leaves round each
/// folder and how much of the tree it draws, and the names written on it, above a preview of the look
/// they make. See <see cref="MapAppearanceWindow"/>, which holds it.
///
/// <para>Each section is a control of its own, made the first time the reader opens it and kept for
/// the life of the window, so a window opened to try one preset builds one section.</para>
/// </summary>
public sealed partial class MapAppearanceView : UserControl
{
    /// <summary>Each section made so far, by the item that opens it.</summary>
    private readonly Dictionary<SelectorBarItem, FrameworkElement> _built = [];

    public MapAppearanceView(MapAppearanceViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();

        Open(PresetsItem);
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

    /// <summary>
    /// Show the section the reader chose and only that one, scrolled to its top: a section opened part
    /// way down, at the place the last one was left, starts the reader in the middle of it.
    /// </summary>
    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is { } chosen)
        {
            Open(chosen);
        }

        SectionScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>Show the section <paramref name="item"/> opens, made now if it never was, and hide every other.</summary>
    private void Open(SelectorBarItem item)
    {
        if (!_built.TryGetValue(item, out var section))
        {
            section = Make(item);
            _built.Add(item, section);
            SectionHost.Children.Add(section);
        }

        foreach (var built in _built.Values)
        {
            built.Visibility = ReferenceEquals(built, section) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The section <paramref name="item"/> opens.</summary>
    private FrameworkElement Make(SelectorBarItem item) =>
        item == ColoursItem ? new MapColoursSection(ViewModel)
        : item == ShapesItem ? new MapShapesSection(ViewModel.Shapes)
        : item == LinesItem ? new MapLinesSection(ViewModel.Lines)
        : item == LayoutItem ? new MapLayoutSection(ViewModel)
        : item == NamesItem ? new MapNamesSection(ViewModel.Names)
        : new MapPresetsSection(ViewModel);

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
