using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Memory;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
/// looking at has no reason to ask Windows anything.</para>
/// </summary>
public sealed partial class MemoryPage : Page
{
    private CancellationTokenSource? _watching;
    private bool _onScreen;

    /// <summary>Whether the page is writing the list's own selection. See <see cref="IsUserSelecting"/>.</summary>
    private bool _showingSelectedRow;

    /// <summary>
    /// Whether the user has touched the list since it last settled on rows it was given. See
    /// <see cref="IsUserSelecting"/>.
    /// </summary>
    private bool _touchedSinceSettled;

    /// <summary>
    /// Whether a pointer is down <em>on a row</em> right now, which is a gesture in progress rather
    /// than a list that has settled.
    ///
    /// <para>A <c>ListView</c> commits a pointer selection on the <em>release</em>, and this page
    /// takes a reading every couple of seconds, so a press held across one would otherwise have its
    /// gesture cleared before the control reported it — and the click would be refused, the highlight
    /// snapping back to whatever was picked before. Explore carries the same term, against a scan's
    /// snapshots rather than against a poll.</para>
    ///
    /// <para>A row is the whole of it. The list's background and its scroll chrome are hit-testable
    /// and commit no selection at all, so a press there has nothing to protect, and suspending the
    /// guard for the length of a scrollbar drag would admit exactly the write §7.2 forbids. It is
    /// also the press the list cannot promise an end for: a <c>ListViewItem</c> captures the
    /// pointer and so guarantees a release or a capture loss, and a press on the background
    /// released somewhere else raises neither.</para>
    /// </summary>
    private bool _pointerDown;

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
                    () => new ContentDialogMemoryConfirmation(XamlRoot, ActualTheme))));

        ViewModel.ViewChanged += (_, _) =>
        {
            ShowCurrentNode();

            // The rows changed rather than the selection, so the highlight goes back onto whichever
            // of the new rows the selection still names. ShowCurrentNode has already put the map's
            // outline on the new drawing.
            ShowSelectedRow();

            // Whatever the list reports from here until the user touches it again is the list's own
            // doing, however long it takes to arrive. See IsUserSelecting.
            Settled();
        };

        ViewModel.Selection.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(MemorySelection.Node))
            {
                ShowSelection();
            }
        };

        InitializeComponent();

        // Required rather than Enabled, as the other destinations are: Enabled is subject to the
        // frame's cache size and Required is not, and a page rebuilt on return loses which picture
        // the reader chose.
        NavigationCacheMode = NavigationCacheMode.Required;

        Map.Hovered += (_, what) => ViewModel.Hover(what.Node, what.AggregateBytes);
        Map.Activated += (_, node) => ViewModel.Descend(node);
        Map.Picked += (_, node) => ViewModel.Selection.Select(node);

        // Both screens are wired the same way and share the one answer, because only one of them is
        // ever on screen: the guard is about whether the user has touched the rows, and there is one
        // user. A TreeViewItem is a ListViewItem, so the same handlers read both.
        Hear(RowsList);
        Hear(TreeList);

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

    /// <summary>
    /// Hear every gesture <paramref name="rows"/> is given, so the page can tell a selection the
    /// user made from one the control made answering a rewrite. See <see cref="IsUserSelecting"/>.
    /// </summary>
    private void Hear(Control rows)
    {
        // Past the handled flag, because a ListViewItem marks a pointer press handled before an
        // ordinary handler on the control would see it.
        rows.AddHandler(PointerPressedEvent, new PointerEventHandler(OnRowsPressed), handledEventsToo: true);

        // Every way a press ends, because the gesture is over whichever way it went and only one of
        // the three fires when the pointer is taken away from the rows mid-click: a capture loss
        // when something else claims it, and a cancellation when the touch is abandoned. A press
        // whose end goes unheard leaves _pointerDown standing and the guard suspended.
        rows.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnRowsReleased), handledEventsToo: true);
        rows.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnRowsReleased), handledEventsToo: true);
        rows.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnRowsReleased), handledEventsToo: true);

        rows.PreviewKeyDown += OnRowsTouched;

        // The other moment a list settles on its own, and the one a rewrite does not cover: its
        // containers are built again when it comes back into the tree, on a return to a page held by
        // NavigationCacheMode.
        rows.Loaded += (_, _) => Settled();
    }

    /// <summary>
    /// Whether a selection the list reported is the user's own gesture.
    ///
    /// <para>ExplorePage's question, and this page needs it more than that one does: a
    /// <c>ListView</c> drops an item from its own selection when the collection under it stops
    /// holding that item where it was, and reports that back as a selection change — and these rows
    /// are rewritten every couple of seconds rather than once a scan. Taken for a gesture, such a
    /// report would leave a program picked that nobody picked, with §7.2.1's one action pointed at
    /// it, and §7.2 says Memory never pre-selects anything.</para>
    ///
    /// <para>The third term is not a window in time. It asks whether the user has touched the list
    /// since it last settled on rows it was given, which is a question with an answer however late
    /// the control's report arrives — and a reading landing part-way through a press does not answer
    /// it, because <see cref="Settled"/> leaves a gesture in progress alone. A row selected straight
    /// through UI Automation carries no gesture at all and is refused with the rest, and both the
    /// keyboard and the pointer reach every row.</para>
    /// </summary>
    private bool IsUserSelecting =>
        !_showingSelectedRow && !ViewModel.IsShowingRows && _touchedSinceSettled;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _onScreen = true;

        if (XamlRoot is { } root)
        {
            root.Changed += OnRootChanged;
        }

        WatchWhileAnyoneCanSeeThis();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _onScreen = false;

        if (XamlRoot is { } root)
        {
            root.Changed -= OnRootChanged;
        }

        StopWatching();

        // One of §7.2.1's three ends of a watch. The close itself is not called off — the messages
        // have gone — and what it comes to is still written to the report.
        ViewModel.Selection.Leave();
    }

    /// <summary>
    /// The window was minimised, restored, resized or moved between displays. Only the first two
    /// matter here, and the property answers all four.
    /// </summary>
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        WatchWhileAnyoneCanSeeThis();

    /// <summary>
    /// Read while this page is on screen and the window is not hidden, and not otherwise.
    ///
    /// <para>Minimised, the window keeps its content sized and its elements visible, so nothing else
    /// here would stop it: a Deguffer left minimised on this page would go on reading the machine,
    /// building a tree and shading every pixel of the picture every couple of seconds, for nobody.</para>
    ///
    /// <para>Started and stopped only on a change of answer. A resize raises this event too, and
    /// restarting the loop each time would put the cadence back to nothing every time a window edge
    /// moved.</para>
    /// </summary>
    private void WatchWhileAnyoneCanSeeThis()
    {
        var wanted = _onScreen && XamlRoot is not { IsHostVisible: false };

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
    /// Draw the current node.
    ///
    /// <para>The colours are asked for per repaint rather than handed over once, which is what the
    /// map's clock reading is for elsewhere. Here they never change: a memory tree has no dates, so
    /// its shapes say which part they belong to.</para>
    /// </summary>
    private void ShowCurrentNode() =>
        Map.Show(ViewModel.Tree, ViewModel.CurrentNode, ViewModel.SelectedView, _ => ShapeColours.ByBranch, ViewModel.LabelFor);

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
        _showingSelectedRow = true;

        try
        {
            // Both, though only one is on screen: the one that is hidden has to agree with what is
            // picked before the reader switches to it, which is what OnViewSelectionChanged relies
            // on rather than repeating.
            RowsList.SelectedItem = listed;
            TreeList.SelectedItem = opened;
        }
        finally
        {
            _showingSelectedRow = false;

            // A write of the page's own is not the user touching the rows. See IsUserSelecting.
            Settled();
        }
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The user has put a hand on the list. Marked as the gesture arrives and before the control acts
    /// on it, so a genuine click or arrow key counts on the first press rather than the second.
    /// </summary>
    private void OnRowsTouched(object sender, RoutedEventArgs e) => _touchedSinceSettled = true;

    /// <summary>
    /// A press. Assigned rather than only set true, so that a press landing anywhere else on the
    /// list answers the question afresh and nothing an earlier press left can outlive it. See
    /// <see cref="_pointerDown"/>.
    /// </summary>
    private void OnRowsPressed(object sender, PointerRoutedEventArgs e)
    {
        _pointerDown = Container(e.OriginalSource) is not null;

        OnRowsTouched(sender, e);
    }

    /// <summary>
    /// The press is over. The gesture is deliberately <em>not</em> cleared here: the
    /// <c>ListView</c> commits a pointer selection on the release, so the report this page is waiting
    /// for arrives immediately after this.
    /// </summary>
    private void OnRowsReleased(object sender, PointerRoutedEventArgs e) => _pointerDown = false;

    /// <summary>
    /// The list has settled on rows it was given, so whatever it reports next is its own doing —
    /// unless a press is still in progress, which is a gesture the user has not finished making. See
    /// <see cref="_pointerDown"/>.
    /// </summary>
    private void Settled()
    {
        if (!_pointerDown)
        {
            _touchedSinceSettled = false;
        }
    }

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
        if (IsUserSelecting)
        {
            ViewModel.Selection.Select(row?.Node);

            return;
        }

        // Refused, and the rows are still showing it. Nothing to take off while one of the page's own
        // writes is in flight, because that write ends by putting the highlight where it belongs. A
        // report arriving outside one is the control having highlighted a row on its own, and
        // leaving that standing is the pre-selection §7.2 forbids, one screen further along: the
        // button acts on the view model, so the list would name a program the close is not pointed
        // at. Put back through the queue rather than here, so the list is not written to from inside
        // its own report.
        if (!_showingSelectedRow && !ViewModel.IsShowingRows)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Asked again on arrival. A ListView commits a pointer selection on the release, so
                // a press can land between the two and take the list back; this repair is then
                // about a state that has already gone, and running it would drop that gesture.
                if (!_touchedSinceSettled)
                {
                    ShowSelectedRow();
                }
            });
        }
    }

    /// <summary>
    /// Two clicks open what a row holds, as they do in Explore. A single click picks the row, which
    /// is what §7.2.1's one action is about, so it cannot also mean "go inside".
    /// </summary>
    private void OnRowsDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {

        if (Container(e.OriginalSource) is { Content: MemoryRow row } && row.HasChildren)
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
            && Container(e.OriginalSource) is { Content: MemoryRow row }
            && row.HasChildren)
        {
            ViewModel.Descend(row.Node);

            e.Handled = true;
        }
    }

    /// <summary>
    /// The chevron on a list row. It opens the row and does not pick it: the button takes the press,
    /// so the list reports no selection for it, and a single click on the row itself still means "I
    /// pick this one". <see cref="Container"/> is what keeps the press the button's.
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

    /// <summary>
    /// The row container a gesture landed in, or null where it landed outside one — or on a button
    /// inside the row, which acts on the gesture itself.
    ///
    /// <para>The chevron is such a button, and one Tab from a selected row puts the keyboard on it.
    /// The row''s own key handler is registered past the handled flag, so without this the Enter the
    /// chevron has already turned into a click arrives there as well and opens the row a second
    /// time — against a container the first open has already rebound to one of the rows that
    /// arrived. Measured: one keypress descended two levels.</para>
    /// </summary>
    private static ListViewItem? Container(object? source)
    {
        for (var element = source as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
        {


            if (element is ButtonBase)
            {
                return null;
            }

            if (element is ListViewItem container)
            {
                return container;
            }
        }

        return null;
    }
}
