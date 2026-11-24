using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Deguffer.App.Views;

/// <summary>The Storage page's Scan, Cancel and Elevate and scan. See the XAML for why they are a control of their own.</summary>
public sealed partial class StorageScanButtons : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(CleanViewModel),
        typeof(StorageScanButtons),
        new PropertyMetadata(null, (buttons, e) =>
        {
            if (e.NewValue is CleanViewModel viewModel)
            {
                ((StorageScanButtons)buttons).OfferChoice(viewModel);
            }
        }));

    public StorageScanButtons() => InitializeComponent();

    /// <summary>Set once, by the Storage page, as it binds.</summary>
    public CleanViewModel? ViewModel
    {
        get => (CleanViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    /// Which way the drop-down opens. The page draws these buttons near the top of the window in the
    /// empty card and at its foot under the list, and the list of locations is tall, so each opens
    /// it towards the room it has.
    /// </summary>
    public FlyoutPlacementMode ChoicePlacement
    {
        get => ScanChoiceFlyout.Placement;
        set => ScanChoiceFlyout.Placement = value;
    }

    /// <summary>The drop-down's locations, over the page's own choice, so both places show and change the same ticks.</summary>
    private void OfferChoice(CleanViewModel viewModel)
    {
        var choice = new ScanChoiceView(viewModel.ScanChoice, viewModel.PreviewChosenCommand);

        // The scan has started from the drop-down, and the page under it is where it reports.
        choice.ScanClicked += (_, _) => ScanChoiceFlyout.Hide();
        ScanChoiceFlyout.Content = choice;
    }
}
