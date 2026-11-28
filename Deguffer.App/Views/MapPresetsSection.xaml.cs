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

    /// <summary>
    /// Choose the preset clicked, the one already selected included. A list raises no change of
    /// selection for the item it already has, so without this a reader who changed the colouring and
    /// chose the preset again would get nothing back. A preset chosen twice in one click, by the
    /// selection and by this, is applied the second time to a look that is already its own, which
    /// changes nothing.
    /// </summary>
    private void OnPresetClicked(object sender, ItemClickEventArgs e) =>
        ViewModel.PresetIndex = PresetList.Items.IndexOf(e.ClickedItem);
}
