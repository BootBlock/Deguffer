using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>The map appearance window's Presets section: the looks a reader can take in one choice. See <see cref="MapAppearanceView"/>.</summary>
public sealed partial class MapPresetsSection : UserControl
{
    public MapPresetsSection(MapAppearanceViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    public MapAppearanceViewModel ViewModel { get; }

    private void OnRestoreClicked(object sender, RoutedEventArgs e) => ViewModel.RestoreDefaults();
}
