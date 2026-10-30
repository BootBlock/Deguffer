using System.Windows.Input;
using Deguffer.App.Controls;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Safety;
using Deguffer.Core.Viewing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

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
    /// <summary>
    /// Tells the reader's selection in the list from one the list made as the page rewrote it. The
    /// rows are rewritten on every snapshot a running scan publishes, and §7.1 says Explore never
    /// pre-selects. See <see cref="ListGestures"/>.
    /// </summary>
    private readonly ListSelectionGuard _guard;

    /// <summary>
    /// Where the list sits before <see cref="OnNotesResized"/> moves its bottom edge. Read once,
    /// because every later read would be of a value this page had already written.
    /// </summary>
    private readonly Thickness _rowsMargin;

    /// <summary>
    /// How the maps look: each picture's colours, the treemap's spacing, the finish and the light. The
    /// application's one, shared with the appearance window, so the window changes the same value the
    /// map is drawn from.
    /// </summary>
    private readonly MapAppearanceViewModel _appearance = App.MapAppearance;

    /// <summary>The cards beside the map, joined to it. See <see cref="ExploreCards"/>.</summary>
    private readonly ExploreCards _cards;

    public ExplorePage()
    {
        // Assigned before InitializeComponent so no x:Bind can evaluate against a null view-model,
        // whatever the framework's initialisation order does next.
        ViewModel = new ExploreViewModel(
            new ExploreScanner(UserEnvironment.Current.VolumeTables, tuning: App.ScanTuning),
            VolumeInventory.Current,
            WindowsHiddenSpace.Default,
            TimeProvider.System,

            // The dialog is built per ask, as the Storage page's is: a XamlRoot captured in this
            // constructor would be the one from before a theme change or a reparent.
            ExploreActions.ForThisMachine(
                () => new ContentDialogExploreConfirmation(XamlRoot, ActualTheme), App.Faults, App.Running),

            // Built synchronously, where the policy above is built in the background: that one
            // constructs every provider and runs their probes, and this one reads a few environment
            // variables, WSL's registration and Docker Desktop's small settings file. It is asked about
            // every row of every directory the page opens, so it has to exist before the first scan
            // finishes.
            ItemGuide.ForThisMachine(),
            ElevatedRelaunch.IsElevated,
            ElevatedRelaunch.TryRelaunch,
            App.Running,
            App.Work,
            App.ScanHistory,
            SystemMotion.Current);

        _guard = new ListSelectionGuard(() => ViewModel.IsShowingRows, restore => DispatcherQueue.TryEnqueueGuarded(restore));

        ViewModel.Elevation.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();

        // The Files layout's rows arrive on their own, after a search rather than with a redraw, so
        // the highlight is put back on them the way a redraw puts it back on the folder's rows.
        ViewModel.Files.Changed += (_, _) =>
        {
            ShowSelectedRows();
            _guard.Settle();
        };
        ViewModel.ViewChanged += (_, _) =>
        {
            ShowCurrentNode();

            // The rows changed rather than the selection, so this is the other direction of the same
            // agreement: the highlight goes back onto whichever of the new rows the selection still
            // names. ShowCurrentNode has already put the map's outlines on the new drawing.
            ShowSelectedRows();

            // Whatever the list reports from here until the user touches it again is the list's own
            // doing, however long it takes to arrive. See ListGestures.
            _guard.Settle();
        };

        InitializeComponent();

        // A row's share bar comes in from nothing as the row itself arrives. Never as a row only
        // scrolls into sight.
        ListAnimation.Play(RowsList, SystemMotion.Current).Arrived += BarMotion.GrowIn;

        _cards = new ExploreCards(ViewModel, Map, GrowthList, TypesList, UsedSpaceStrip);

        // Kept alive by the page's events and the replay's, which it follows for the page's life.
        _ = new ExploreReplayClock(ViewModel.Replay, this);

        _rowsMargin = RowsList.Margin;

        // Subscribed past the handled flag, because a ListViewItem marks the right-tap handled on
        // its way past and an ordinary handler on the list never runs. Measured: an attached
        // ContextFlyout never appears either, and ContextRequested does not arrive at all. The map
        // needs none of this — nothing between it and the pointer handles anything.
        RowsList.AddHandler(
            RightTappedEvent, new RightTappedEventHandler(OnRowsRightTapped), handledEventsToo: true);

        // The two ways a person moves this list's selection, marked as they arrive rather than
        // inferred afterwards. The guard settles as the list's containers are built again on a return
        // to the page, and ShowAs settles it as the list comes back from behind the map.
        _guard.Hear(RowsList);

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
            else if (changed.PropertyName == nameof(ExploreSelection.Removing))
            {
                Map.MarkRemoving(ViewModel.Selection.Removing.Tree, ViewModel.Selection.Removing.Nodes);
            }
        };

        Map.Hovered += (_, hit) => ViewModel.Hover(hit);
        Map.Activated += (_, node) => ViewModel.Descend(node);
        Map.Ascending += (_, _) => Invoke(ViewModel.AscendCommand);
        Map.Facts = ViewModel.ShapeFacts;
        Map.Removal = ViewModel.ShapeRemoval;
        Map.ContrastChanged += (_, _) => ViewModel.MapInContrast = Map.Contrasted;
        Map.ZoomingIn += (_, from) => ViewModel.Visiting.Zoomed(from);
        Map.ViewportChanged += (_, _) => ViewModel.Visiting.ViewportChanged();
        Map.Picked += (_, node) => ViewModel.Selection.Select(node is { } picked ? [picked] : []);
        Map.MenuRequested += OnMapMenuRequested;

        // A step away remembers the zoom it left, so Back comes back to it. See ExploreVisiting.Viewing.
        ViewModel.Visiting.Viewing = () => Map.Viewport;

        // The mouse's Back and Forward buttons from anywhere on the page, as a browser takes them.
        // Heard past the handled flag, because a row or a button under the pointer marks the press
        // handled on its way past. Backspace is the page's accelerator, in the XAML.
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPageReleased), handledEventsToo: true);

        // Read once, here, and never again — the same rule the Storage page's density selector
        // follows, and for the same reason: re-reading on every navigation undoes a choice whose
        // write to disk failed, silently and in the same session.
        var preferences = App.Preferences.Current;

        ViewModel.Notes.Dismissed = preferences.ExploreNotesDismissed;

        // Kept as the reader leaves it, on the overview itself.
        Map.OverviewHidden = preferences.ExploreOverviewHidden;
        Map.OverviewHiddenChanged += (_, _) =>
            App.Preferences.Update(current => current with { ExploreOverviewHidden = Map.OverviewHidden });

        // The look before the colouring and the view below, so the legend they build is in the
        // colours the map is drawn in. The view-model reads each picture's scheme from it.
        ViewModel.Look = _appearance.Look;

        _appearance.Changed += (_, _) =>
        {
            ViewModel.Look = _appearance.Look;
            ShowCurrentNode();
        };

        // A preset whose colours are part of its look moves the Colour box, which applies and writes
        // the colouring as a choice made in it does.
        _appearance.ColouringChosen += (_, colouring) => ColourAs(colouring);

        // The colouring first, because ShowAs draws the map and the map has to be told what its
        // colours mean before it paints rather than after. Both as the appearance holds them, which
        // read them from the preferences and holds a colouring a preset chose before this page was
        // built, written or not.
        ColourAs(_appearance.Colouring);
        ShowAs(_appearance.View);

        // A scan of a full drive takes long enough that throwing it away on a trip to Settings and
        // back would be its own defect.
        NavigationCacheMode = NavigationCacheMode.Required;

        // An elevated replacement opens straight here, and is pointed where the instance it replaced
        // was pointed once its drives have been read, as it is first shown (OnNavigatedTo).
        _resuming = ElevatedRelaunch.Requested as ExploreRequest;
    }

    /// <summary>
    /// Where an elevated relaunch asked this page to point, until the first showing has done so.
    /// </summary>
    private ExploreRequest? _resuming;

    public ExploreViewModel ViewModel { get; }

    /// <summary>
    /// Open the appearance window, or bring it forward where it is already open.
    ///
    /// <para>Its own window rather than a dialog on this page, because the reader has to see the map
    /// change as they choose: a dialog covers the page it belongs to. See
    /// <see cref="MapAppearanceWindow"/>.</para>
    /// </summary>
    private void OnAppearanceClicked(object sender, RoutedEventArgs e) => App.ShowMapAppearance();

    /// <summary>
    /// Draw the page in <paramref name="view"/>, one of <see cref="ExploreChoice.Views"/>, and leave
    /// the selector agreeing with what is on screen.
    ///
    /// Safe to call from the selector's own change handler: the first call moves the index off -1
    /// and re-enters that handler once, which lands back here with the index it now holds, and
    /// assigning an unchanged index raises nothing further.
    /// </summary>
    private void ShowAs(ExploreView view)
    {
        ViewModel.SelectedView = view;
        ViewSelector.SelectedIndex = ExploreChoice.IndexOf(view);

        // Each picture keeps its own colours, so the appearance window's picker moves to speak for
        // the new one.
        _appearance.View = view;

        var listed = view is ExploreView.List or ExploreView.Files;

        if (listed)
        {
            ShowRowsOf(view);
        }

        RowsList.Visibility = listed ? Visibility.Visible : Visibility.Collapsed;
        Map.Visibility = listed ? Visibility.Collapsed : Visibility.Visible;

        if (listed)
        {
            // Back from behind the map, so the list realises its containers again and may settle on
            // one. Nothing here need write the highlight, so no other settle covers it (see
            // ListGestures).
            _guard.Settle();
        }

        ShowCurrentNode();
    }

    /// <summary>
    /// Give the list the rows of <paramref name="view"/>: the folder's children for the List layout,
    /// and the largest files below it for the Files layout.
    ///
    /// <para>One list for both, so the rules above guarding its selection are written once. The Files
    /// layout takes one row at a time, which is what keeps "select all" off it: §7.1 allows a
    /// selection of several things picked out by hand, and Ctrl+A or a Shift range over a filtered
    /// list is the filter picking them.</para>
    /// </summary>
    private void ShowRowsOf(ExploreView view)
    {
        var files = view == ExploreView.Files;
        object rows = files ? ViewModel.Files.Rows : ViewModel.Listing.Rows;

        if (ReferenceEquals(RowsList.ItemsSource, rows))
        {
            return;
        }

        // The list reports every row it drops on the way, and none of that is the user's doing.
        _guard.Write(() =>
        {
            RowsList.ItemsSource = null;
            RowsList.SelectionMode = files ? ListViewSelectionMode.Single : ListViewSelectionMode.Extended;
            RowsList.ItemTemplate = (DataTemplate)Resources[files ? "FileRow" : "FolderRow"];
            RowsList.ItemsSource = rows;
        });

        ShowSelectedRows();
    }

    /// <summary>The rows the list is showing, whichever layout gave them.</summary>
    private IEnumerable<IExploreListed> ListedRows =>
        RowsList.ItemsSource as IEnumerable<IExploreListed> ?? [];

    /// <summary>
    /// Colour the map by <paramref name="colouring"/>, and leave that selector agreeing with what is
    /// on screen. Re-entrant on the same terms as <see cref="ShowAs"/>.
    /// </summary>
    private void ColourAs(ExploreColouring colouring)
    {
        ViewModel.SelectedColouring = colouring;
        ColourSelector.SelectedIndex = (int)colouring;
        _appearance.Colouring = colouring;

        ShowCurrentNode();
    }

    /// <summary>
    /// Draw the map. It puts its own outlines back on the new drawing, because it was told what is
    /// selected when the selection last changed and that has not stopped being true.
    /// </summary>
    private void ShowCurrentNode()
    {
        // A step of the replay of what grew, in place of the tree now: the same folder, as the map
        // coloured by growth paints it, with the volume beside it as it was at that step (#285).
        if (ViewModel.Replay.Frame is { } frame)
        {
            Map.Show(
                frame,
                ViewModel.CurrentNode,
                ViewModel.SelectedView,
                _ => ShapeColours.ForReplay(frame),
                frame.Label,
                ViewModel.Look,
                frame.Replay.VolumeAt(frame.Step, ViewModel.VolumeBeside));

            _cards.Relight();
            return;
        }

        Map.Show(
            ViewModel.Tree,
            ViewModel.CurrentNode,
            ViewModel.SelectedView,
            ViewModel.SelectedColouring,
            ViewModel.SelectedScheme,
            ViewModel.Look,
            ViewModel.VolumeBeside,
            ViewModel.Growth.Comparison,
            ViewModel.Types.Dominant,
            ViewModel.Visiting.Revisiting);

        _cards.Relight();
    }

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

        if (RowsList.SelectedItems.OfType<IExploreListed>().Select(row => row.Node).ToHashSet().SetEquals(picked))
        {
            return;
        }

        var shown = ListedRows.Where(row => picked.Contains(row.Node)).ToList();

        // Writing these back raises SelectionChanged, once for the clear and once per row. That is
        // this method's own doing rather than the user's, and letting it round-trip would report a
        // half-applied selection back to the view model as a gesture.
        _guard.Write(() =>
        {
            if (RowsList.SelectionMode == ListViewSelectionMode.Single)
            {
                // A list that holds one row at a time can show a selection of one, and nothing else
                // without misstating it. Several picked in the folder list stay picked, and the line
                // under the card names them.
                //
                // Written through SelectedItem alone. A single-selection ListView refuses any write to
                // SelectedItems with E_UNEXPECTED, Clear included, and every removal ends here with
                // an emptied selection.
                RowsList.SelectedItem = picked.Count == 1 && shown is [var only] ? only : null;
            }
            else
            {
                RowsList.SelectedItems.Clear();

                foreach (var row in shown)
                {
                    RowsList.SelectedItems.Add(row);
                }
            }
        });
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
        ViewModel.Notes.Dismissed = dismissed;
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
        if (ViewSelector.SelectedIndex is not (>= 0 and var index) || index >= ExploreChoice.Views.Count)
        {
            return;
        }

        var view = ExploreChoice.Views[index];

        ShowAs(view);
        App.Preferences.Update(current => current with { Explore = view });
    }

    /// <summary>
    /// Read the volumes again as the picker opens, where the last reading has gone stale, so its
    /// space figures describe the disk now rather than when the page was first shown. See
    /// <see cref="ExploreTargeting.RefreshDrivesAsync"/>.
    ///
    /// <para>Not cancelled when the page is left. NavigationCacheMode keeps the page, so rows written
    /// after it is left are the rows its next visit shows.</para>
    /// </summary>
    private async void OnDriveListOpened(object sender, object e) => await ViewModel.Targeting.RefreshDrivesAsync(CancellationToken.None);

    /// <summary>
    /// Read the volumes as the page is shown, on the same terms as the picker opening, the first
    /// visit included. The page is kept alive by NavigationCacheMode.
    ///
    /// <para>The first showing of an elevated replacement then points where the instance it replaced
    /// was pointed, and scans. Only where that target survived the relaunch: ShownAsync says so, and
    /// a drive that has since gone leaves the page waiting rather than scanning something else.</para>
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var resuming = _resuming;
        _resuming = null;

        if (!await ViewModel.Targeting.ShownAsync(resuming, CancellationToken.None))
        {
            return;
        }

        // Started once the page is live, as the Storage page starts its own: the scan reports back
        // through the dispatcher, and starting it before then would report into nothing.
        if (IsLoaded)
        {
            StartRequestedScan(this, new RoutedEventArgs());
        }
        else
        {
            Loaded += StartRequestedScan;
        }
    }

    /// <summary>Applied first and persisted second, for the reason above.</summary>
    private void OnColourSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var colouring = (ExploreColouring)ColourSelector.SelectedIndex;

        ColourAs(colouring);
        App.Preferences.Update(current => current with { ExploreColours = colouring });
    }

    /// <summary>Open the folder a row of the list of what grew names. See <see cref="ExploreViewModel.ShowFolder"/>.</summary>
    private void OnGrowthRowClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ExploreGrowthRow row)
        {
            ViewModel.ShowFolder(row);
        }
    }

    /// <summary>The pointer came over a row of either card beside the map. See <see cref="ExploreCards"/>.</summary>
    private void OnCardRowEntered(object sender, PointerRoutedEventArgs e) =>
        _cards.Entered(((FrameworkElement)sender).DataContext);

    private void OnCardRowExited(object sender, PointerRoutedEventArgs e) =>
        _cards.Exited(((FrameworkElement)sender).DataContext);

    /// <summary>
    /// List the files of the kind a row of the breakdown names, in the Files layout. The filter is the
    /// view-model's, and the view box is this page's, so the two are set here in that order.
    /// </summary>
    private void OnTypeRowClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ExploreTypeRow row)
        {
            ViewModel.Files.ListOnly(row.Category);
            ShowAs(ExploreView.Files);
        }
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

        if (await FolderDialog.ChooseAsync(window) is { } folder)
        {
            ViewModel.Targeting.ScopeTo(folder);
        }
    }

    private void OnCrumbClicked(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { DataContext: ExploreCrumb crumb })
        {
            ViewModel.GoTo(crumb.Position);
        }
    }

    /// <summary>
    /// The list's selection is the view-model's selection. Sent as nodes rather than as rows,
    /// because the map selects things that have no row.
    /// </summary>
    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_guard.Admits(restore: ShowSelectedRows))
        {
            ViewModel.Selection.Select([.. RowsList.SelectedItems.OfType<IExploreListed>().Select(r => r.Node)]);
        }
    }

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
        if (ListSelectionGuard.Row(e.OriginalSource) is not { Content: IExploreListed row })
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
        if (ListSelectionGuard.Row(e.OriginalSource) is not { Content: IExploreListed row } container)
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
    /// The mouse's Back or Forward button. Taken as it comes up rather than as it goes down, as
    /// Windows takes it: the list is not then in the middle of a press, which it would read as the
    /// reader picking a row out of the rows the step is about to replace. See
    /// <see cref="ListGestures"/>.
    /// </summary>
    private void OnPageReleased(object sender, PointerRoutedEventArgs e)
    {
        var command = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.XButton1Released => ViewModel.Visiting.GoBackCommand,
            PointerUpdateKind.XButton2Released => ViewModel.Visiting.GoForwardCommand,
            _ => null,
        };

        if (command is not null && Invoke(command))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Backspace goes back, unless a box being typed in has the focus: there it deletes a character,
    /// and leaving the accelerator unhandled passes the key on to the box.
    /// </summary>
    private void OnBackspaceInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
        {
            return;
        }

        args.Handled = Invoke(ViewModel.Visiting.GoBackCommand);
    }

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
