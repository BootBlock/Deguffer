using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The Storage page's selection menu. What each entry does, and when it is offered, is
/// <see cref="StorageSelection"/>'s.
/// </summary>
public sealed partial class StorageSelectionMenu : UserControl
{
    public static readonly DependencyProperty SelectionProperty =
        DependencyProperty.Register(nameof(Selection), typeof(StorageSelection), typeof(StorageSelectionMenu), new PropertyMetadata(null));

    public StorageSelectionMenu() => InitializeComponent();

    /// <summary>The page's selection, whose commands the entries run.</summary>
    public StorageSelection? Selection
    {
        get => (StorageSelection?)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }
}
