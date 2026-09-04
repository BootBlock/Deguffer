using System.Windows.Input;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Knowledge;
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
    /// <summary>Whether <see cref="ShowSelectedRows"/> is writing the list's selection.</summary>
    private bool _showingSelectedRows;

    /// <summary>
    /// Whether the user has touched the list since it last settled on rows or containers it was
    /// given. See <see cref="IsUserSelecting"/>.
    /// </summary>
    private bool _touchedSinceSettled;

    /// <summary>
    /// Where the list sits before <see cref="OnNotesResized"/> moves its bottom edge. Read once,
    /// because every later read would be of a value this page had already written.
    /// </summary>
    private readonly Thickness _rowsMargin;

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
                () => new ContentDialogExploreConfirmation(XamlRoot, ActualTheme)),

            // Built here rather than lazily, unlike the policy above: that one constructs every
            // provider, and this one reads four environment variables. It is asked about every row
            // of every directory the page opens, so it has to exist before the first scan
            // finishes.
            ItemGuide.ForThisMachine());

        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();
        ViewModel.ViewChanged += (_, _) =>
        {
            ShowCurrentNode();

            // The rows changed rather than the selection, so this is the other direction of the same
            // agreement: the highlight goes back onto whichever of the new rows the selection still
            // names. ShowCurrentNode has already put the map's outlines on the new drawing.
            ShowSelectedRows();

            // Whatever the list reports from here until the user touches it again is the list's own
            // doing, however long it takes to arrive. See IsUserSelecting.
            _touchedSinceSettled = false;
        };

        InitializeComponent();

        _rowsMargin = RowsList.Margin;

        // Subscribed past the handled flag, because a ListViewItem marks the right-tap handled on
        // its way past and an ordinary handler on the list never runs. Measured: an attached
        // ContextFlyout never appears either, and ContextRequested does not arrive at all. The map
        // needs none of this — nothing between it and the pointer handles anything.
        RowsList.AddHandler(
            RightTappedEvent, new RightTappedEventHandler(OnRowsRightTapped), handledEventsToo: true);

        // The two ways a person moves this list's selection, marked as they arrive rather than
        // inferred afterwards. Both run before the control has changed anything: a pointer press is
        // subscribed past the handled flag, as the right-tap above is and for the same reason, and
        // the keyboard is taken on the preview because a ListView handles the arrow keys itself and
        // an ordinary KeyDown handler would run after the selection had already moved. See
        // IsUserSelecting.
        RowsList.AddHandler(
            PointerPressedEvent, new PointerEventHandler(OnRowsTouched), handledEventsToo: true);

        RowsList.PreviewKeyDown += OnRowsTouched;

        // The other moment the list settles on its own, and the one a rewrite does not cover: its
        // containers are built again when it comes back into the tree, on a return to a page held
        // by NavigationCacheMode. ShowAs covers the same thing for the map's half of the toggle.
        // See IsUserSelecting.
        RowsList.Loaded += (_, _) => _touchedSinceSettled = false;

        // One signal, followed by both screens that show a selection. The list is not on screen
        // while the map is and keeps whatever was highlighted in it until something says otherwise,
        // and the map draws an outline round what was picked — so a change reaching only one of them
        // is a Delete pointed at something the other one is still showing. Here rather than at each
        // call site, because the callers are five and growing: a map click, a row click, a
        // navigation, a snapshot carried over mid-scan, and the end of a removal.
        ViewModel.Selection.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(ExploreSelection.Nodes))
            {
                ShowSelection();
            }
        };

        // Once, because the rule does not change — only what it answers, as things are removed. The
        // map draws descendants the list never lists, so it is the one screen that can put a shape
        // under the pointer whose folder has already gone.
        Map.Excluding(ViewModel.Selection.WasRemoved);

        Map.Hovered += (_, what) => ViewModel.Hover(what.Node, what.AggregateBytes);
        Map.Activated += (_, node) => ViewModel.Descend(node);
        Map.Picked += (_, node) => ViewModel.Selection.Select(node is { } picked ? [picked] : []);
        Map.MenuRequested += OnMapMenuRequested;

        // Read once, here, and never again — the same rule the Storage page's density selector
        // follows, and for the same reason: re-reading on every navigation undoes a choice whose
        // write to disk failed, silently and in the same session.
        var preferences = App.Preferences.Current;

        ViewModel.NotesDismissed = preferences.ExploreNotesDismissed;

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

        if (listed)
        {
            // Back from behind the map, so the list realises its containers again and may settle on
            // one. Nothing here writes the highlight, so no other reset covers it (see
            // IsUserSelecting).
            _touchedSinceSettled = false;
        }

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

    /// <summary>
    /// Draw the map. It puts its own outlines back on the new drawing, because it was told what is
    /// selected when the selection last changed and that has not stopped being true.
    /// </summary>
    private void ShowCurrentNode() =>
        Map.Show(ViewModel.Tree, ViewModel.CurrentNode, ViewModel.SelectedView, ViewModel.SelectedColouring);

    /// <summary>
    /// Put both screens back in step with what is actually selected: the outline on the map, and the
    /// highlight in the list.
    /// </summary>
    private void ShowSelection()
    {
        Map.Select(ViewModel.Selection.Nodes);
        ShowSelectedRows();
    }

    /// <summary>
    /// Put the list's highlight back on what the view model says is selected.
    ///
    /// <para>The two are separate copies of one fact, and only the view model's survives a change
    /// to the rows: a <c>ListView</c> drops an item from <c>SelectedItems</c> when the collection
    /// under it stops holding that item where it was, which is every rebuild and every removal.
    /// They also part company with no change to the rows at all, because the map selects while the
    /// list is not on screen. Leaving them to disagree is the safety-relevant half — the menu and
    /// the accelerators act on the view model's selection, so a highlight that has quietly gone, or
    /// one left on the wrong row, is a Delete pointed at something nothing on screen
    /// identifies.</para>
    ///
    /// <para>One way only. The view model's copy can name a node with no row at all, because a map
    /// hit lands on descendants several levels below the current node, so the list is a subset of
    /// it by design rather than a second opinion on it.</para>
    /// </summary>
    private void ShowSelectedRows()
    {
        // A set, because the loop below asks it once per row. As a list that is a scan of the whole
        // selection per row — a folder of five thousand entries against a few hundred picked ones is
        // over a million comparisons, on a path that runs at every click (G4).
        var picked = ViewModel.Selection.Nodes.ToHashSet();

        if (RowsList.SelectedItems.OfType<ExploreRow>().Select(row => row.Node).ToHashSet().SetEquals(picked))
        {
            return;
        }

        // Writing these back raises SelectionChanged, once for the clear and once per row. That is
        // this method's own doing rather than the user's, and letting it round-trip would report a
        // half-applied selection back to the view model as a gesture.
        _showingSelectedRows = true;

        try
        {
            RowsList.SelectedItems.Clear();

            foreach (var row in ViewModel.Rows.Where(row => picked.Contains(row.Node)))
            {
                RowsList.SelectedItems.Add(row);
            }
        }
        finally
        {
            _showingSelectedRows = false;

            // A write of the page's own is not the user touching the list. See IsUserSelecting.
            _touchedSinceSettled = false;
        }
    }

    /// <summary>
    /// End the list above whatever the notes are covering.
    ///
    /// <para>The notes float over the card rather than sitting below it, so that opening one cannot
    /// resize the drawing underneath — see the comment on <c>Notes</c> in the XAML. The map keeps
    /// working underneath them, because they let the pointer through. The list cannot be treated
    /// that way: a row under a note is a row nothing can retrieve, since <c>ScrollIntoView</c> and
    /// the keyboard both stop as soon as a row is anywhere inside the viewport. Extending what the
    /// list may scroll through is therefore not enough, and the viewport itself has to end above
    /// them.</para>
    ///
    /// <para>Measured against what they occupy rather than against the worst case, so the list gives
    /// the room up only while there is something in them. Nothing here can loop: the notes are
    /// bottom-aligned in the same grid and take their size from their own content, so the list's
    /// height is not an input to theirs.</para>
    /// </summary>
    private void OnNotesResized(object sender, SizeChangedEventArgs e) =>
        RowsList.Margin = new Thickness(
            _rowsMargin.Left,
            _rowsMargin.Top,
            _rowsMargin.Right,
            _rowsMargin.Bottom + (e.NewSize.Height > 0 ? e.NewSize.Height + Notes.Margin.Bottom : 0));

    /// <summary>
    /// Put the notes away, leaving the button that brings them back.
    ///
    /// <para>Applied first and persisted second, on the reasoning the two selectors below give:
    /// that order is inverted from <see cref="Shell.PreferenceService"/>'s usual one for a setting
    /// that governs what is drawn rather than what is deleted, and a reader closing something that
    /// is over their picture should not find it still there because a file could not be
    /// written.</para>
    /// </summary>
    private void OnDismissNotes(object sender, RoutedEventArgs e) => KeepNotes(dismissed: true);

    /// <summary>Ask for them back. The other half of <see cref="OnDismissNotes"/>.</summary>
    private void OnShowNotes(object sender, RoutedEventArgs e) => KeepNotes(dismissed: false);

    private void KeepNotes(bool dismissed)
    {
        ViewModel.NotesDismissed = dismissed;
        App.Preferences.Update(current => current with { ExploreNotesDismissed = dismissed });

        // The button that was just pressed is the one that has gone, so focus would be left on a
        // collapsed element and the reader would have to cross the whole page to reach the other.
        // §7 asks for a sentence to stay reachable "by whatever the reader is using", and a keyboard
        // is one of those. The corner keeps the focus instead, whichever way the toggle went.
        _ = (dismissed ? ShowNotes : DismissNotes).Focus(FocusState.Programmatic);
    }

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
    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsUserSelecting)
        {
            ViewModel.Selection.Select([.. RowsList.SelectedItems.OfType<ExploreRow>().Select(r => r.Node)]);

            return;
        }

        // Refused, and the list is still showing it. Nothing to take off while one of the page's own
        // writes is in flight, because that write ends by calling ShowSelectedRows itself. A report
        // arriving outside one is the control having highlighted a row on its own, and leaving that
        // standing is the same pre-selection §7.1 forbids, one screen further along: the menu and
        // the accelerators act on the view model, so the list would name a folder Delete is not
        // pointed at. Put back through the queue rather than here, so the list is not written to
        // from inside its own report.
        if (!_showingSelectedRows && !ViewModel.IsShowingRows)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Asked again on arrival. A ListView commits a pointer selection on the release, so
                // a press can land between the two and take the list back; this repair is then
                // about a state that has already gone, and running it would drop that gesture.
                if (!_touchedSinceSettled)
                {
                    ShowSelectedRows();
                }
            });
        }
    }

    /// <summary>Note that the user has touched the list. See <see cref="IsUserSelecting"/>.</summary>
    private void OnRowsTouched(object sender, RoutedEventArgs e) => _touchedSinceSettled = true;

    /// <summary>
    /// Whether a selection change arriving from the list is the user's doing.
    ///
    /// <para>Two windows are not, and both are this page's own writing. While the view model is
    /// rewriting the rows, the ListView drops every item it can no longer place and reports each
    /// drop; while <see cref="ShowSelectedRows"/> is writing the highlight, so does every
    /// intermediate state of that write. Taken for a gesture, either replaces the selection the
    /// user made with whatever the rewrite had reached — and on a rescan those are node numbers
    /// belonging to the tree before it, which the arriving one cannot place at all.</para>
    ///
    /// <para><b>Neither window closes when the page stops writing.</b> A <c>ListView</c> settles on
    /// rows and containers it has just been given during a later layout pass, and it can select a
    /// row of its own accord there — reported after <c>Show</c> has returned and after both flags
    /// above have gone false. The page then took that for a gesture, and §7.1's "Explore never
    /// pre-selects" stopped being true: the status line named a folder nobody had picked, and the
    /// menu and the accelerators act on exactly that selection. Reported against a navigation into
    /// a folder holding one entry, where the control puts a focused index back and the only row
    /// there is is the one it lands on. <b>The control's own write could not be provoked on
    /// demand</b>, under driven input, on this code or on the code the report was filed against, so
    /// what was measured is the page's response to such a write rather than the control making
    /// one.</para>
    ///
    /// <para>So the third term is not a window at all. It asks whether the user has touched the
    /// list since it last settled on rows or containers it was given, which is a question with an
    /// answer however late the report is. Both gestures that move a selection are marked as they
    /// arrive and before the control acts on them, so a genuine click or arrow key counts on the
    /// first press after a navigation rather than the second.</para>
    ///
    /// <para>A row selected straight through UI Automation carries neither, and is refused with the
    /// rest. From in here it cannot be told apart from the control's own write, and §7.1's
    /// direction is to drop a pick nobody can attribute rather than to act on one. The keyboard and
    /// the pointer both reach every row, so nothing is unreachable by that.</para>
    /// </summary>
    private bool IsUserSelecting =>
        !_showingSelectedRows && !ViewModel.IsShowingRows && _touchedSinceSettled;

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
