using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>The last clean's figures on the Storage page's result card. See the XAML for why they stand apart.</summary>
public sealed partial class RunFiguresView : UserControl
{
    public static readonly DependencyProperty ResultProperty =
        DependencyProperty.Register(nameof(Result), typeof(RunResultViewModel), typeof(RunFiguresView), new PropertyMetadata(null));

    public RunFiguresView() => InitializeComponent();

    /// <summary>Set once, by the Storage page, as it binds.</summary>
    public RunResultViewModel? Result
    {
        get => (RunResultViewModel?)GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }
}
