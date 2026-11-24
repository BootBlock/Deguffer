using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The line each location gets under the last clean's safety check. See the XAML for why the lines are
/// folded and when they open.
/// </summary>
public sealed partial class RunRowsView : UserControl
{
    public static readonly DependencyProperty ResultProperty =
        DependencyProperty.Register(nameof(Result), typeof(RunResultViewModel), typeof(RunRowsView), new PropertyMetadata(null));

    public RunRowsView()
    {
        InitializeComponent();
        ListAnimation.Play(RowsList, SystemMotion.Current);
    }

    /// <summary>Set once, by the Storage page, as it binds.</summary>
    public RunResultViewModel? Result
    {
        get => (RunResultViewModel?)GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }
}
