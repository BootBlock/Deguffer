using System.Windows.Input;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Memory;
using Deguffer.Core.Startup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
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
            App.Work,
            StartupFolderReader.Current.FolderOf,
            ShellHandoff.Windows);

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

        // Past the handled flag, because a ListViewItem marks the right-tap handled on its way past
        // and an ordinary handler on the list never runs.
        EntryList.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(OnEntryRightTapped), handledEventsToo: true);
        Accelerate(VirtualKey.Application, VirtualKeyModifiers.None, OnMenuKeyInvoked);
        Accelerate(VirtualKey.F10, VirtualKeyModifiers.Shift, OnMenuKeyInvoked);
        Accelerate(VirtualKey.Enter, VirtualKeyModifiers.Control, ViewModel.Menu.RevealProgramCommand);
        Accelerate(VirtualKey.Enter, VirtualKeyModifiers.Menu, ViewModel.Menu.PropertiesCommand);
        Accelerate(VirtualKey.C, VirtualKeyModifiers.Control, ViewModel.Menu.CopyCommand);

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

    /// <summary>
    /// Open the row menu on the row the pointer landed on, picking that row first, so the menu never
    /// acts on a row other than the one it was opened on (§7.5). A right-tap on the list's background
    /// or on a row's own button opens nothing.
    /// </summary>
    private void OnEntryRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ListSelectionGuard.Row(e.OriginalSource) is not { Content: StartupRow row } container)
        {
            return;
        }

        EntryList.SelectedItem = row;
        container.Focus(FocusState.Programmatic);
        EntryMenu.ShowAt(EntryList, new FlyoutShowOptions { Position = e.GetPosition(EntryList) });
        e.Handled = true;
    }

    /// <summary>The context-menu key and Shift+F10 open the row menu on the focused row, placed on the row rather than at the pointer.</summary>
    private void OnMenuKeyInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (PickFocusedRow() is { } container)
        {
            EntryMenu.ShowAt(container);
            args.Handled = true;
        }
    }

    /// <summary>
    /// A key that runs <paramref name="command"/> on the focused row, handled only where it ran, so a
    /// key that does not apply goes on.
    /// </summary>
    private void Accelerate(VirtualKey key, VirtualKeyModifiers modifiers, ICommand command) =>
        Accelerate(key, modifiers, (_, args) => args.Handled = PickFocusedRow() is not null && command.TryExecute());

    /// <summary>
    /// Pick the row the keyboard focus is inside, a button on the row included, and return its container,
    /// or null where focus is in no row of this list.
    ///
    /// <para>The accelerators are heard wherever focus is on the page, as Explore's are, so a key must
    /// not act at all from outside the rows: Ctrl+C in the change report's selectable text copies that
    /// text, not a row's command. Inside the rows it acts on the row the reader can see is focused,
    /// never on one picked earlier and out of view, because focus and selection part whenever focus
    /// moves without selecting, as Ctrl with an arrow key does (§7.5).</para>
    /// </summary>
    private ListViewItem? PickFocusedRow()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused
            || VisualTree.FromSelf(focused).OfType<ListViewItem>().FirstOrDefault() is not { } container
            || EntryList.ItemFromContainer(container) is not StartupRow row)
        {
            return null;
        }

        EntryList.SelectedItem = row;
        return container;
    }

    private void Accelerate(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> invoked)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += invoked;
        EntryList.KeyboardAccelerators.Add(accelerator);
    }
}
