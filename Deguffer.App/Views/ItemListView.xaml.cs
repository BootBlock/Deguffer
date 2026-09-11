using System.ComponentModel;
using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;

namespace Deguffer.App.Views;

/// <summary>
/// One row's items on the Storage page, in place of the rows: searched, grouped and chosen one by one.
///
/// <para>A control built in code by the page, for the reason <see cref="ProviderInfoView"/> is: every
/// claim it makes comes from the row through <see cref="ItemListViewModel"/>, and nothing here decides
/// anything.</para>
/// </summary>
public sealed partial class ItemListView : UserControl
{
    private readonly Action<FindingViewModel, StepViewModel> _toggleKeep;
    private readonly Action _close;
    private readonly CollectionViewSource _grouped;

    /// <summary>
    /// Assigned before InitializeComponent, so no x:Bind can evaluate against a null model, the same
    /// order CleanPage relies on.
    /// </summary>
    /// <param name="toggleKeep">
    /// Keeps or releases one item. The page's, because the keep list is shared by every row and the
    /// page's totals move with it.
    /// </param>
    /// <param name="close">Puts the page's rows back.</param>
    public ItemListView(
        ItemListViewModel viewModel,
        Action<FindingViewModel, StepViewModel> toggleKeep,
        Action close)
    {
        ViewModel = viewModel;
        _toggleKeep = toggleKeep;
        _close = close;
        InitializeComponent();

        _grouped = (CollectionViewSource)Resources["GroupedItems"];
        ListShownItems();

        // Subscribed for the life of this control, which is the life of the list: the page discards
        // both together when the rows come back.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // The search is what a reader of a long list reaches for first, so the keyboard starts there.
        Loaded += (_, _) => SearchBox.Focus(FocusState.Programmatic);
    }

    public ItemListViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ItemListViewModel.Shown))
        {
            ListShownItems();
        }
    }

    private void ListShownItems()
    {
        if (ViewModel.IsGrouped)
        {
            _grouped.Source = ViewModel.Shown;
            RowsList.ItemsSource = _grouped.View;
        }
        else
        {
            RowsList.ItemsSource = ViewModel.ShownItems;
        }
    }

    private void OnKeepClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StepViewModel step })
        {
            _toggleKeep(ViewModel.Row, step);
        }
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => _close();

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _close();
    }
}
