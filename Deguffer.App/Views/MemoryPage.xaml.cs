using Deguffer.App.Controls;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Memory;
using Deguffer.Core.Startup;
using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Deguffer.App.Views;

/// <summary>
/// The Memory page: where physical memory is, drawn by the same layouts that draw a scanned drive.
///
/// <para>It answers "where is the memory going", which §7.2 makes a question of its own: Storage
/// answers what is safe to remove, Explore where the space went. This page shows and explains, and
/// has exactly one action on what it shows — asking one program the user picked to close itself
/// (§7.2.1). It never classifies, never pre-selects, and never orders anything by how closable it
/// is.</para>
///
/// <para>It reads the machine while someone can see it, and stops otherwise, because a page nobody is
/// looking at has no reason to ask Windows anything. See <see cref="WatchWhileAnyoneCanSeeThis"/>.</para>
/// </summary>
public sealed partial class MemoryPage : Page
{
    private CancellationTokenSource? _watching;

    /// <summary>Whether anyone can see the page, which decides whether it reads the machine.</summary>
    private readonly ElementSight _sight;

    /// <summary>The look the map was last drawn in. See <see cref="FollowLook"/>.</summary>
    private MapLook _look = App.MapAppearance.Look;

    /// <summary>
    /// Tells the reader's pick in the list or the tree from one the control made as the page rewrote
    /// the rows, which it does every couple of seconds. §7.2 says Memory never pre-selects anything.
    /// See <see cref="ListGestures"/>.
    /// </summary>
    private readonly ListSelectionGuard _guard;

    /// <summary>Ends the startup read begun as the page opened, when the page is left before it lands.</summary>
    private CancellationTokenSource? _startupRead;

    public MemoryPage()
    {
        // Assigned before InitializeComponent so no x:Bind can evaluate against a null view-model,
        // which is the order ExplorePage settled on for the same reason.
        ViewModel = new MemoryViewModel(
            new MemoryFeed(MemorySource.Default, TimeProvider.System),

            // The dialog is built per ask, as Explore's and Storage's are: a XamlRoot captured in
            // this constructor would be the one from before a theme change or a reparent.
            new MemorySelection(
                MemoryActions.ForThisMachine(
                    () => new ContentDialogMemoryConfirmation(XamlRoot, ActualTheme)),
                App.Faults));

        _sight = new ElementSight(this, WatchWhileAnyoneCanSeeThis);

        _guard = new ListSelectionGuard(() => ViewModel.IsShowingRows, restore => DispatcherQueue.TryEnqueueGuarded(restore));

        ViewModel.ViewChanged += (_, _) =>
        {
            ShowCurrentNode();

            // The rows changed rather than the selection, so the highlight goes back onto whichever
            // of the new rows the selection still names. ShowCurrentNode has already put the map's
            // outline on the new drawing.
            ShowSelectedRow();

            // Whatever the list reports from here until the user touches it again is the list's own
            // doing, however long it takes to arrive. See ListGestures.
            _guard.Settle();
        };

        ViewModel.Selection.StartupEntryRequested += (_, entry) => App.ShowStartupEntry(entry);

        ViewModel.Selection.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(MemorySelection.Node))
            {
                ShowSelection();
            }
        };

        InitializeComponent();

        // A row's share bar comes in from nothing as the row itself arrives, in the tree as a row
        // opens as well. Never as a row only scrolls into sight.
        ListAnimation.Play(RowsList, SystemMotion.Current).Arrived += BarMotion.GrowIn;
        ListAnimation.Play(TreeList, SystemMotion.Current, BarMotion.GrowIn);

        // Required rather than Enabled, as the other destinations are: Enabled is subject to the
        // frame's cache size and Required is not, and a page rebuilt on return loses which picture
        // the reader chose.
        NavigationCacheMode = NavigationCacheMode.Required;

        MapWiring.Attach(
            Map,
            ViewModel,
            new DrawnSetting<bool>(
                App.Preferences,
                current => current.MemoryOverviewHidden,
                (current, hidden) => current with { MemoryOverviewHidden = hidden },
                hidden => Map.OverviewHidden = hidden));

        // Both screens share the one guard, because only one of them is ever on screen.
        _guard.Hear(RowsList);
        _guard.Hear(TreeList);

        // Past the handled flag as well, and measured: a ListViewItem marks Enter handled while
        // deciding what to do about its own selection, so an ordinary KeyDown handler on the list
        // never sees the key at all. Only the list: in the tree a row opens where it sits, and the
        // control's own keys already do that.
        RowsList.AddHandler(
            KeyDownEvent, new KeyEventHandler(OnRowsKeyDown), handledEventsToo: true);

        ViewSelector.SelectedIndex = (int)ViewModel.SelectedView;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public MemoryViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FollowLook();
        App.MapAppearance.Changed += OnLookChanged;

        _sight.Follow();
        WatchWhileAnyoneCanSeeThis();

        _ = KnowStartupAsync();
    }

    /// <summary>
    /// Read what Windows starts at sign-in, once each time the page is opened rather than at the
    /// page's cadence (§7.5), off the window's thread because it reads the registry, two folders and
    /// every package's manifest.
    /// </summary>
    private async Task KnowStartupAsync()
    {
        _startupRead?.Cancel();
        using var read = new CancellationTokenSource();
        _startupRead = read;

        try
        {
            var reading = await Task.Run(() => StartupReader.Default.Read(read.Token), read.Token);

            ViewModel.Selection.KnowStartup(reading);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
            // The page was left, or opened again, before the list arrived: the next visit reads it.
        }
        finally
        {
            if (ReferenceEquals(_startupRead, read))
            {
                _startupRead = null;
            }
        }
    }

    private void OnStartupLinkClick(Hyperlink sender, HyperlinkClickEventArgs args) =>
        ViewModel.Selection.ShowStartupCommand.Execute(null);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.MapAppearance.Changed -= OnLookChanged;

        _sight.Stop();
        StopWatching();

        // One of §7.2.1's three ends of a watch. The close itself is not called off — the messages
        // have gone — and what it comes to is still written to the report.
        ViewModel.Selection.Leave();

        _startupRead?.Cancel();
    }

    /// <summary>
    /// Read while anyone can see this page, and not otherwise (<see cref="Sights"/>).
    ///
    /// <para>Minimised, or behind other windows, the window keeps its content sized and its elements
    /// visible, so nothing else here would stop it: a Deguffer left on this page under other windows
    /// would go on reading the machine, building a tree and shading every pixel of the picture every
    /// couple of seconds, for nobody.</para>
    ///
    /// <para>A page uncovered or restored starts a reading at once rather than at the next beat of the
    /// cadence, so the reader never sees the list from before it was hidden presented as the
    /// present.</para>
    ///
    /// <para>Started and stopped only on a change of answer. A resize or a window moving elsewhere on the
    /// desktop asks again too, and restarting the loop each time would put the cadence back to nothing
    /// every time a window edge moved.</para>
    /// </summary>
    private void WatchWhileAnyoneCanSeeThis()
    {
        var wanted = _sight.Now == Sight.Seen;

        if (wanted == (_watching is not null))
        {
            return;
        }

        if (wanted)
        {
            Watch();
        }
        else
        {
            StopWatching();
        }
    }

    private void Watch()
    {
        StopWatching();

        _watching = new CancellationTokenSource();

        // Not awaited: this is a loop that runs for as long as the page is being looked at, and it
        // takes its own failures (see MemoryViewModel.WatchAsync).
        _ = ViewModel.WatchAsync(_watching.Token);
    }

    private void StopWatching()
    {
        _watching?.Cancel();
        _watching?.Dispose();
        _watching = null;
    }

    private void OnViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var view = (ExploreView)ViewSelector.SelectedIndex;

        ViewModel.SelectedView = view;

        // The list, the tree and the picture are the same contents, so exactly one is on screen. The
        // map is hidden rather than told to stop, and it draws nothing while it is hidden.
        RowsList.Visibility = Shown(view == ExploreView.List);
        TreeList.Visibility = Shown(view == ExploreView.Tree);
        Map.Visibility = Shown(view is not (ExploreView.List or ExploreView.Tree));

        ShowCurrentNode();

        // The screen that was hidden kept whatever it was last told, and the one arriving has to
        // agree with what is actually picked before the user can act on it.
        ShowSelection();
    }

    /// <summary>
    /// Draw again if the colours, the treemap spacing, the finish of the shapes or the light changed.
    /// Asked on every visit and at every change while the page is on screen, because all of them are
    /// chosen elsewhere: in the map appearance window, which can stay open while this page is on
    /// screen, and the spacing on the Settings page as well, which the appearance follows.
    ///
    /// <para>The application's look rather than the preferences file's, so a choice whose write
    /// failed is drawn here as it is on the Explore page.</para>
    /// </summary>
    private void FollowLook()
    {
        var look = App.MapAppearance.Look;

        if (look != _look)
        {
            _look = look;
            ShowCurrentNode();
        }
    }

    private void OnLookChanged(object? sender, EventArgs e) => FollowLook();

    /// <summary>
    /// Draw the current node.
    ///
    /// <para>The colours are asked for per repaint rather than handed over once, which is what the
    /// map's clock reading is for elsewhere. Here they change only with the scheme: a memory tree has
    /// no dates, so its shapes say which part they belong to.</para>
    /// </summary>
    private void ShowCurrentNode()
    {
        var colours = ShapeColours.ByBranch(_look.SchemeFor(ViewModel.SelectedView));

        Map.Show(
            ViewModel.Tree,
            ViewModel.CurrentNode,
            ViewModel.SelectedView,
            _ => colours,
            ViewModel.CaptionFor,
            _look,
            VolumeSpace.None);
    }

    /// <summary>Put both screens back in step with what is selected: the outline on the map, and the highlight in the list.</summary>
    private void ShowSelection()
    {
        Map.Select(ViewModel.Selection.Node is { } node ? [node] : []);
        ShowSelectedRow();
    }

    /// <summary>
    /// Put the list's highlight back on what the view model says is picked.
    ///
    /// <para>One way only. The view model's copy can name a node with no row at all, because a hit on
    /// the picture lands on descendants several levels below the node on screen, so the list is a
    /// subset of it by design rather than a second opinion on it.</para>
    /// </summary>
    private void ShowSelectedRow()
    {
        var picked = ViewModel.Selection.Node;
        var listed = picked is { } node ? ViewModel.Rows.FirstOrDefault(row => row.Node == node) : null;
        var opened = picked is { } deep ? ViewModel.Opened(deep) : null;

        if (ReferenceEquals(RowsList.SelectedItem, listed) && ReferenceEquals(TreeList.SelectedItem, opened))
        {
            return;
        }

        // Writing this back raises SelectionChanged. That is the page's own doing rather than the
        // user's, and letting it round-trip would report the page's write back as a gesture.
        _guard.Write(() =>
        {
            // Both, though only one is on screen: the one that is hidden has to agree with what is
            // picked before the reader switches to it, which is what OnViewSelectionChanged relies
            // on rather than repeating.
            RowsList.SelectedItem = listed;
            TreeList.SelectedItem = opened;
        });
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Picked(RowsList.SelectedItem as MemoryRow);

    /// <summary>
    /// The tree's own selection, read from the report rather than from the control: a
    /// <c>TreeView</c> raises this before it writes <c>SelectedItem</c>, so asking the control here
    /// answers that nothing is selected and takes the reader's pick off again. Measured on Windows
    /// App SDK 1.8.
    /// </summary>
    private void OnTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args) =>
        Picked(args.AddedItems.FirstOrDefault() as MemoryRow);

    /// <summary>
    /// A screen's selection is the view model's selection, where the user made it. Sent as a node
    /// rather than as a row, because the picture selects things that have no row.
    /// </summary>
    private void Picked(MemoryRow? row)
    {
        if (_guard.Admits(restore: ShowSelectedRow))
        {
            ViewModel.Selection.Select(row?.Node);
        }
    }

    /// <summary>
    /// Two clicks open what a row holds, as they do in Explore. A single click picks the row, which
    /// is what §7.2.1's one action is about, so it cannot also mean "go inside".
    /// </summary>
    private void OnRowsDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {

        if (ListSelectionGuard.Row(e.OriginalSource) is { Content: MemoryRow row } && row.HasChildren)
        {
            ViewModel.Descend(row.Node);
        }
    }

    /// <summary>
    /// Enter opens what a row holds, which is the double click above for a reader using the keyboard.
    ///
    /// <para>The list is the route through the tree for somebody who cannot use the picture, so it
    /// cannot be the one view where going inside something needs a pointer. Enter on a row that holds
    /// nothing is left alone rather than swallowed, which would make the key look broken rather than
    /// inapplicable.</para>
    /// </summary>
    private void OnRowsKeyDown(object sender, KeyRoutedEventArgs e)
    {

        if (e.Key == VirtualKey.Enter
            && ListSelectionGuard.Row(e.OriginalSource) is { Content: MemoryRow row }
            && row.HasChildren)
        {
            ViewModel.Descend(row.Node);

            e.Handled = true;
        }
    }

    /// <summary>
    /// The chevron on a list row. It opens the row and does not pick it: the button takes the press,
    /// so the list reports no selection for it, and a single click on the row itself still means "I
    /// pick this one". <see cref="ListSelectionGuard.Row"/> is what keeps the press the button's.
    /// </summary>
    private void OnOpenRow(object sender, RoutedEventArgs e)
    {

        if (sender is FrameworkElement { DataContext: MemoryRow row })
        {
            ViewModel.Descend(row.Node);
        }
    }

    /// <summary>
    /// A row in the tree was opened. Its own contents are already filled in — that is what put the
    /// expander on it — and what those hold is filled in now, so the rows arriving carry their own
    /// expanders rather than waiting up to a reading for one.
    /// </summary>
    private void OnRowExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is MemoryRow row)
        {
            row.IsExpanded = true;

            ViewModel.Open(row);
        }
    }

    /// <summary>
    /// A row in the tree was closed. Recorded so that readings stop filling in what is under it: the
    /// control keeps the rows it has, and the next reading brings them back up to date if it is
    /// opened again.
    /// </summary>
    private void OnRowCollapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Item is MemoryRow row)
        {
            row.IsExpanded = false;
        }
    }

    private void OnAscend(object sender, RoutedEventArgs e) => ViewModel.Ascend();


    private void OnCrumbClicked(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { DataContext: MemoryCrumb crumb })
        {
            ViewModel.GoTo(crumb);
        }
    }
}
