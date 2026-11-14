using System.Windows.Input;
using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Views;

/// <summary>
/// The Scan button's drop-down: the locations to choose from, and the button that scans the chosen
/// ones. What is chosen, stored and shown is <see cref="ScanChoiceViewModel"/>'s.
/// </summary>
public sealed partial class ScanChoiceView : UserControl
{
    /// <summary>
    /// Both assigned before InitializeComponent, so no x:Bind can evaluate against a null choice
    /// whatever the framework's initialisation order does next, as in <see cref="CleanConfirmationView"/>.
    /// </summary>
    /// <param name="scanCommand">The scan of the ticked locations, which the Storage page owns.</param>
    public ScanChoiceView(ScanChoiceViewModel choice, ICommand scanCommand)
    {
        Choice = choice;
        ScanCommand = scanCommand;
        InitializeComponent();

        var groups = (CollectionViewSource)Resources["Groups"];
        groups.Source = choice.Shown;
        LocationList.ItemsSource = groups.View;
    }

    /// <summary>Raised when the scan button is clicked, before its command runs, so the owner can close the drop-down.</summary>
    public event EventHandler? ScanClicked;

    public ScanChoiceViewModel Choice { get; }

    public ICommand ScanCommand { get; }

    private void OnScanClicked(object sender, RoutedEventArgs e) => ScanClicked?.Invoke(this, EventArgs.Empty);
}
