using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Memory;
using Deguffer.Core.Startup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Deguffer.App.Views;

/// <summary>
/// The Startup destination (§7.5). Wires the view-model to the machine and carries the list's
/// selection to it; every decision is Core's.
/// </summary>
public sealed partial class StartupPage : Page
{
    /// <summary>Windows' own Startup page in Settings, which Microsoft documents as a launch target.</summary>
    private static readonly Uri SettingsStartup = new("ms-settings:startupapps");

    /// <summary>What the entries are ordered by, chosen in the Sort by box.</summary>
    private readonly DrawnSetting<StartupOrder> _order;

    /// <summary>Which way round that order runs, chosen with the button beside it.</summary>
    private readonly DrawnSetting<StartupOrderDirection> _direction;

    private bool _hasRead;

    public StartupPage()
    {
        var switcher = new StartupSwitcher(
            WindowsStartupRegistry.Default,
            StartupFolderReader.Current,
            TimeProvider.System);

        // Assigned before InitializeComponent so no x:Bind evaluates against a null view-model.
        ViewModel = new StartupViewModel(
            ct => StartupView.Read(StartupReader.Default, MemorySource.Default, ProcessImages.Default, ct),
            entry => switcher.Switch(entry, ElevatedRelaunch.IsElevated),
            ElevatedRelaunch.IsElevated,
            ElevatedRelaunch.TryRelaunch,
            async () => await Launcher.LaunchUriAsync(SettingsStartup),
            App.Running,
            App.Work);

        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();
        ViewModel.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(StartupViewModel.SelectedRow) && !ReferenceEquals(EntryList.SelectedItem, ViewModel.SelectedRow))
            {
                // A link from Memory picked it, so the list follows and brings it into view.
                EntryList.SelectedItem = ViewModel.SelectedRow;

                if (ViewModel.SelectedRow is { } row)
                {
                    EntryList.ScrollIntoView(row);
                }
            }
        };

        InitializeComponent();

        // Shown through the view-model, which reads back the value it settled on, so a stored value
        // no order is named for is written back as the order it is shown as.
        _order = new DrawnSetting<StartupOrder>(
            App.Preferences, current => current.StartupOrder, (current, _) => current with { StartupOrder = ViewModel.Order }, OrderEntries);
        _direction = new DrawnSetting<StartupOrderDirection>(
            App.Preferences, current => current.StartupOrderDirection, (current, _) => current with { StartupOrderDirection = ViewModel.Direction }, value => ViewModel.Direction = value);
        OrderEntries(_order.Stored);
        ViewModel.Direction = _direction.Stored;

        ListAnimation.Play(EntryList, SystemMotion.Current);

        NavigationCacheMode = NavigationCacheMode.Required;

        Loaded += OnLoaded;
    }

    public StartupViewModel ViewModel { get; }

    /// <summary>A link from Memory names the entry to pick, which the reading picks once it holds it.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is StartupKey key)
        {
            ViewModel.Reveal(key);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Once: a return to the cached page keeps the list the reader left, and Refresh reads again.
        if (_hasRead)
        {
            return;
        }

        _hasRead = true;
        _ = ViewModel.RefreshAsync();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SelectedRow = EntryList.SelectedItem as StartupRow;

    /// <summary>Order the entries by <paramref name="order"/>, and leave the box agreeing with the order the view-model settled on.</summary>
    private void OrderEntries(StartupOrder order)
    {
        ViewModel.Order = order;
        OrderSelector.SelectedIndex = (int)ViewModel.Order;
    }

    private void OnOrderSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _order.Choose((StartupOrder)OrderSelector.SelectedIndex);

    private void OnDirectionClicked(object sender, RoutedEventArgs e) =>
        _direction.Choose(ViewModel.IsDescending ? StartupOrderDirection.Ascending : StartupOrderDirection.Descending);

    private void OnReportClosed(InfoBar sender, object args) => ViewModel.DismissReportCommand.Execute(null);
}
