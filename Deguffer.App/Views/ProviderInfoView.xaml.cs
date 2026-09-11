using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// What one Storage row actually is: whose files these are, what they are for, whether to remove
/// them, and — on the second tab — what removing them would do.
///
/// It is a control rather than a panel built in code for the reason
/// <see cref="CleanConfirmationView"/> is: <c>Application.Current.Resources[key]</c> in C# snapshots
/// the brushes of whatever theme is current and does not follow a repaint. Every claim it makes
/// comes from the provider through the <see cref="FindingViewModel"/>; nothing here decides
/// anything.
/// </summary>
public sealed partial class ProviderInfoView : UserControl
{
    private readonly Action<FindingViewModel> _showItems;

    /// <summary>
    /// Assigned before InitializeComponent, so no x:Bind can evaluate against a null model whatever
    /// the framework's initialisation order does next — the same order CleanPage relies on.
    /// </summary>
    /// <param name="showItems">
    /// Closes the dialog and lists the row's items on the Storage page. The page's rather than this
    /// control's, because the list takes the place of the page's rows.
    /// </param>
    public ProviderInfoView(FindingViewModel finding, Action<FindingViewModel> showItems)
    {
        Finding = finding;
        _showItems = showItems;
        InitializeComponent();
    }

    public FindingViewModel Finding { get; }

    private void OnShowItemsClicked(object sender, RoutedEventArgs e) => _showItems(Finding);
}
