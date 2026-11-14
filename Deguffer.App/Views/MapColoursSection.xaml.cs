using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The map appearance window's Colours section: the palette each picture is drawn in, how strong and
/// how light its colours are, and what a folder round what it holds is filled with. See
/// <see cref="MapAppearanceView"/>.
/// </summary>
public sealed partial class MapColoursSection : UserControl
{
    public MapColoursSection(MapAppearanceViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    public MapAppearanceViewModel ViewModel { get; }
}
