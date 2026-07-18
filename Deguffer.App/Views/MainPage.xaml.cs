using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

public sealed partial class MainPage : UserControl
{
    public MainPage()
    {
        InitializeComponent();
        ViewModel = new MainViewModel();
    }

    public MainViewModel ViewModel { get; }
}
