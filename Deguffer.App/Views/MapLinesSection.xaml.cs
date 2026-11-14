using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>The map appearance window's Lines section: the lines round files and folders. See <see cref="MapAppearanceView"/>.</summary>
public sealed partial class MapLinesSection : UserControl
{
    public MapLinesSection(MapLinesViewModel viewModel)
    {
        ViewModel = viewModel;

        InitializeComponent();
    }

    public MapLinesViewModel ViewModel { get; }
}
