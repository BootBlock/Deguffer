using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Deguffer.App.Views;

/// <summary>The map appearance window's Presets section: the looks a reader can take in one choice. See <see cref="MapAppearanceView"/>.</summary>
public sealed partial class MapPresetsSection : UserControl
{
    /// <summary>The preset selected as the click or key that may choose one began. See <see cref="OnPresetClicked"/>.</summary>
    private int _selectedBefore = -1;

    public MapPresetsSection(MapAppearanceViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();

        // Past the handled flag, because the list's items mark both handled while they select.
        PresetList.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPressStarted), handledEventsToo: true);
        PresetList.AddHandler(KeyDownEvent, new KeyEventHandler(OnPressStarted), handledEventsToo: true);
    }

    public MapAppearanceViewModel ViewModel { get; }

    private void OnRestoreClicked(object sender, RoutedEventArgs e) => ViewModel.RestoreDefaults();

    private void OnPressStarted(object sender, RoutedEventArgs e) => _selectedBefore = PresetList.SelectedIndex;

    /// <summary>
    /// Choose again the preset that was already selected. A list raises no change of selection for
    /// the item it already has, so without this a reader who changed the colouring and chose the
    /// preset again would get nothing back. A preset that was not selected is chosen by the selection
    /// alone, so one click applies it once, whichever of the two events the list raises first.
    /// </summary>
    private void OnPresetClicked(object sender, ItemClickEventArgs e)
    {
        var clicked = PresetList.Items.IndexOf(e.ClickedItem);

        if (clicked >= 0 && clicked == _selectedBefore)
        {
            ViewModel.PresetIndex = clicked;
        }
    }
}
