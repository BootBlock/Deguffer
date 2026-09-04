using System.Windows.Input;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.Storage.Pickers;

namespace Deguffer.App.Views;

/// <summary>
/// The Explore page: pick a drive or a folder, see what is on it, and act on what is in it.
///
/// <para>It answers "what is using the space", which §3 is careful to say is not the same question
/// as "what is safe to remove" — the Storage page answers that one, and it answers it with a
/// provider's knowledge of what a directory actually is rather than with its size. §7.1 is what
/// lets this page act at all, and it is narrow: one selection the user picked out by hand, never a
/// bulk action, and nothing the tier model would call Tier 4.</para>
///
/// <para>What may be removed is decided in Core and re-decided there immediately before anything is
/// deleted. Nothing on this page is the thing standing between a size picture and
/// <c>C:\Windows</c>.</para>
/// </summary>
public sealed partial class ExplorePage : Page
{
    public ExplorePage()
    {
        // Assigned before InitializeComponent so no x:Bind can evaluate against a null view-model,
        // whatever the framework's initialisation order does next.
        ViewModel = new ExploreViewModel(
            ExploreScanner.Default,
            VolumeInventory.Current,

            // The dialog is built per ask, as the Storage page's is: a XamlRoot captured in this
            // constructor would be the one from before a theme change or a reparent.
            ExploreActions.ForThisMachine(
                () => new ContentDialogExploreConfirmation(XamlRoot, ActualTheme)));

        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();
        ViewModel.ViewChanged += (_, _) => ShowCurrentNode();

        InitializeComponent();

        // Subscribed past the handled flag, because a ListViewItem marks the right-tap handled on
        // its way past and an ordinary handler on the list never runs. Measured: an attached
        // ContextFlyout never appears either, and ContextRequested does not arrive at all. The map
        // needs none of this — nothing between it and the pointer handles anything.
        RowsList.AddHandler(
            RightTappedEvent, new RightTappedEventHandler(OnRowsRightTapped), handledEventsToo: true);

        Map.Hovered += (_, what) => ViewModel.Hover(what.Node, what.AggregateBytes);
        Map.Activated += (_, node) => ViewModel.Descend(node);
        Map.Picked += (_, node) => ViewModel.Selection.Select(node is { } picked ? [picked] : []);
        Map.MenuRequested += OnMapMenuRequested;

        // Read once, here, and never again — the same rule the Storage page's density selector
        // follows, and for the same reason: re-reading on every navigation undoes a choice whose
        // write to disk failed, silently and in the same session.
        var preferences = App.Preferences.Current;

        // The colouring first, because ShowAs draws the map and the map has to be told what its
        // colours mean before it paints rather than after.
        ColourAs(preferences.ExploreColours);
        ShowAs(preferences.Explore);

        // A scan of a full drive takes long enough that throwing it away on a trip to Settings and
        // back would be its own defect.
        NavigationCacheMode = NavigationCacheMode.Required;

        // An elevated replacement opens straight here, pointed where the instance it replaced was
        // pointed. Only where that target survived the relaunch: PointAt says so, and a drive that
        // has since gone leaves the page waiting rather than scanning something else.
        if (ElevatedRelaunch.Requested is ExploreRequest requested
            && ViewModel.PointAt(requested.Drive, requested.Folder))
        {
            // Deferred to Loaded rather than run here, as the Storage page defers its own: the scan
            // reports back through the dispatcher, and starting it before the page is live would
            // report into nothing.
            Loaded += StartRequestedScan;
        }
    }

    public ExploreViewModel ViewModel { get; }

    /// <summary>
    /// Draw the page in <paramref name="view"/>, and leave the selector agreeing with what is on
    /// screen.
    ///
    /// Safe to call from the selector's own change handler: the first call moves the index off -1
    /// and re-enters that handler once, which lands back here with the index it now holds, and
    /// assigning an unchanged index raises nothing further.
    /// </summary>
    private void ShowAs(ExploreView view)
    {
        ViewModel.SelectedView = view;
        ViewSelector.SelectedIndex = (int)view;

        var listed = view == ExploreView.List;

        RowsList.Visibility = listed ? Visibility.Visible : Visibility.Collapsed;
        Map.Visibility = listed ? Visibility.Collapsed : Visibility.Visible;

        ShowCurrentNode();
    }

    /// <summary>
    /// Colour the map by <paramref name="colouring"/>, and leave that selector agreeing with what is
    /// on screen. Re-entrant on the same terms as <see cref="ShowAs"/>.
    /// </summary>
    private void ColourAs(ExploreColouring colouring)
    {
        ViewModel.SelectedColouring = colouring;
        ColourSelector.SelectedIndex = (int)colouring;

        ShowCurrentNode();
    }

    private void ShowCurrentNode() =>
        Map.Show(ViewModel.Tree, ViewModel.CurrentNode, ViewModel.SelectedView, ViewModel.SelectedColouring);

    /// <summary>
    /// The view is applied first and persisted second, so it takes effect whether or not the
    /// preferences file can be written — <see cref="Shell.PreferenceService"/>'s usual order is
    /// inverted here on the same reasoning the Storage page gives: that order exists so a rejected
    /// write cannot take effect for a setting governing what gets deleted, and this one governs
    /// which picture is drawn.
    /// </summary>
    private void OnViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var view = (ExploreView)ViewSelector.SelectedIndex;

        ShowAs(view);
        App.Preferences.Update(current => current with { Explore = view });
    }

    /// <summary>
    /// Read the volumes again as the picker opens, so its space figures describe the disk now
    /// rather than when the app started. See <see cref="ExploreViewModel.RefreshDrives"/>.
    /// </summary>
    private void OnDriveListOpened(object sender, object e) => ViewModel.RefreshDrives();

    /// <summary>Applied first and persisted second, for the reason above.</summary>
    private void OnColourSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var colouring = (ExploreColouring)ColourSelector.SelectedIndex;

        ColourAs(colouring);
        App.Preferences.Update(current => current with { ExploreColours = colouring });
    }

    /// <summary>
    /// Scope the next scan to a folder, through the system picker.
    ///
    /// <para>The picking lives here rather than in the view model for the reason
    /// <see cref="SettingsPage"/> gives: it is a WinUI dialog needing a window handle, and the view
    /// model stays testable by knowing only about the path that comes back.</para>
    /// </summary>
    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        // A picker with no owner throws in a WinUI 3 desktop app rather than opening unowned.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            ViewModel.ScopeTo(folder.Path);
        }
    }

    private void OnCrumbClicked(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { Tag: int node })
        {
            ViewModel.GoTo(node);
        }
    }

    /// <summary>
    /// The list's selection is the view-model's selection. Sent as nodes rather than as rows,
    /// because the map selects things that have no row.
    /// </summary>
    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Selection.Select([.. RowsList.SelectedItems.OfType<ExploreRow>().Select(r => r.Node)]);

    /// <summary>
    /// Two clicks go in. A folder is descended into and a file is opened, which is what a double
    /// click means everywhere else the user has done this.
    ///
    /// <para>Taken from the row the gesture landed on, not from the selection. The handler sits on
    /// the list, whose background is hit-testable, so a double-click on the empty space below the
    /// last row arrives here too — and reading the selection instead would open whatever was
    /// highlighted, from a gesture that landed on nothing.</para>
    /// </summary>
    private void OnRowsDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Container(e.OriginalSource) is not { Content: ExploreRow row })
        {
            return;
        }

        if (row.IsDirectory)
        {
            ViewModel.Descend(row.Node);
        }
        else if (ViewModel.Selection.OpenCommand.CanExecute(null))
        {
            ViewModel.Selection.OpenCommand.Execute(null);
        }
    }

    /// <summary>
    /// Open the menu on the row the pointer landed on, moving the selection there first when that
    /// row is not already in it.
    ///
    /// <para>Moving the selection is the safety-relevant half. Without it the menu would act on
    /// whatever was highlighted before, so a right-click on one row could delete another — which is
    /// exactly the mistake <see cref="Deguffer.Core.Exploring.Acting.ExploreRemover"/> re-checks the
    /// policy to survive, and it should not be made here in the first place.</para>
    ///
    /// <para>Subscribed in the constructor rather than declared in XAML, and shown by hand rather
    /// than attached with <c>ContextFlyout</c>. Both were measured: an attached flyout never
    /// appears, <c>ContextRequested</c> does not arrive at all, and a plain <c>RightTapped</c>
    /// handler does not run because a <c>ListViewItem</c> marks the gesture handled first. The
    /// keyboard reaches all five of these actions through the accelerators instead.</para>
    /// </summary>
    private void OnRowsRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Container(e.OriginalSource) is not { Content: ExploreRow row } container)
        {
            // The gesture landed on the list's own background. Clearing is what the map does on the
            // same miss, and the alternative is a menu positioned at the pointer whose Delete is
            // live against a row somewhere else on screen.
            RowsList.SelectedItem = null;
        }
        else if (!RowsList.SelectedItems.Contains(row))
        {
            RowsList.SelectedItem = row;
            container.Focus(FocusState.Programmatic);
        }

        e.Handled = Show(RowsList, e.GetPosition(RowsList));
    }

    /// <summary>
    /// Open the same menu where the pointer is on the map. The map has already reported what is
    /// under it, so this is about the shape the user right-clicked rather than about whatever was
    /// picked before.
    /// </summary>
    private void OnMapMenuRequested(object? sender, Point point) => Show(Map, point);

    private bool Show(FrameworkElement at, Point? point)
    {
        // The list owns the flyout, and the map borrows it. One menu rather than two, because two
        // is how the treemap comes to offer an action the list has stopped offering.
        if (RowsList.ContextFlyout is not MenuFlyout menu)
        {
            return false;
        }

        // A keyboard-raised request has no position, and placing the menu at the pointer's last
        // location would put it wherever the mouse happens to be sitting.
        menu.ShowAt(at, point is { } p ? new FlyoutShowOptions { Position = p } : new FlyoutShowOptions());

        return true;
    }

    /// <summary>
    /// The list container the event started in, or null when the click missed every row.
    ///
    /// Walked up the visual tree because the source is whichever <c>TextBlock</c> or icon was under
    /// the pointer, and the row is several levels above it.
    /// </summary>
    private static ListViewItem? Container(object? source)
    {
        for (var element = source as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is ListViewItem container)
            {
                return container;
            }
        }

        return null;
    }

    private void OnDeleteInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = Invoke(ViewModel.Selection.DeleteCommand);

    private void OnDeletePermanentlyInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = Invoke(ViewModel.Selection.DeletePermanentlyCommand);

    private void OnOpenInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = Invoke(ViewModel.Selection.OpenCommand);

    private void OnPropertiesInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = Invoke(ViewModel.Selection.PropertiesCommand);

    private void OnRevealInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = Invoke(ViewModel.Selection.RevealCommand);

    /// <summary>
    /// Run the command if it will run, and say whether it did.
    ///
    /// <para>Reporting that back is what stops the key going on to the control underneath: swallowing
    /// Enter while nothing is selected would leave the list unable to activate a row, and swallowing
    /// Delete would make the key look broken rather than inapplicable.</para>
    /// </summary>
    private static bool Invoke(ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    /// <summary>Start the scan an elevated replacement was launched to run. See the constructor.</summary>
    private void StartRequestedScan(object sender, RoutedEventArgs e)
    {
        Loaded -= StartRequestedScan;

        if (ViewModel.ScanCommand.CanExecute(null))
        {
            ViewModel.ScanCommand.Execute(null);
        }
    }
}
