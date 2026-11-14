using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>The map appearance window's Names section: what is written on the picture, and how. See <see cref="MapAppearanceView"/>.</summary>
public sealed partial class MapNamesSection : UserControl
{
    public MapNamesSection(MapNamesViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    public MapNamesViewModel ViewModel { get; }
}
