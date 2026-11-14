using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The map appearance window's Layout section: the room round each folder, the smallest shape drawn
/// and how many levels are opened. See <see cref="MapAppearanceView"/>.
/// </summary>
public sealed partial class MapLayoutSection : UserControl
{
    public MapLayoutSection(MapAppearanceViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    public MapAppearanceViewModel ViewModel { get; }
}
