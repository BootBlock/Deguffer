using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>The map appearance window's Shapes section: shading, bevels and the light under the pointer. See <see cref="MapAppearanceView"/>.</summary>
public sealed partial class MapShapesSection : UserControl
{
    public MapShapesSection(MapShapesViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    public MapShapesViewModel ViewModel { get; }
}
