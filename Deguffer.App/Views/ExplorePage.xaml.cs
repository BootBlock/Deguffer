using Deguffer.App.Controls;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Exploring.Links;
using Deguffer.Core.Exploring.Network;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Safety;
using Deguffer.Core.Viewing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

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

    /// <summary>Whether the reader put the notes away. See <see cref="ShowNotesDismissed"/>.</summary>
    private readonly DrawnSetting<bool> _notesDismissed;

    /// <summary>The picture the page draws, chosen in the View box.</summary>
    private readonly DrawnSetting<ExploreView> _view;

    /// <summary>What the map's colours say, chosen in the Colour box.</summary>
    private readonly DrawnSetting<ExploreColouring> _colouring;

    public ExplorePage()
    {
        // Assigned before InitializeComponent so no x:Bind can evaluate against a null view-model,
        // whatever the framework's initialisation order does next.
        ViewModel = new ExploreViewModel(
            new ExploreScanner(UserEnvironment.Current.VolumeTables, tuning: App.ScanTuning),
            VolumeInventory.Current,
            WindowsHiddenSpace.Default,
            LongPathProbe.Default,
            NetworkAccess.Machine,
            App.Preferences,
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
            App.DriveHealth,
            SystemMotion.Current,

            // Low, so the frame that shows the result is drawn before the collection pauses the
            // window.
            () => DispatcherQueue.TryEnqueueGuarded(DispatcherQueuePriority.Low, SpentSnapshots.Release),
            WindowsLinkTargetReader.Default,
            ShortcutFolders.Of(UserEnvironment.Current, SystemDirectories.Current));

        _guard = new ListSelectionGuard(() => ViewModel.IsShowingRows, restore => DispatcherQueue.TryEnqueueGuarded(restore));

        _notesDismissed = new DrawnSetting<bool>(
            App.Preferences,
            current => current.ExploreNotesDismissed,
            (current, dismissed) => current with { ExploreNotesDismissed = dismissed },
            ShowNotesDismissed);

        _view = new DrawnSetting<ExploreView>(
            App.Preferences, current => current.Explore, (current, view) => current with { Explore = view }, ShowAs);

        _colouring = new DrawnSetting<ExploreColouring>(
            App.Preferences,
            current => current.ExploreColours,
            (current, colouring) => current with { ExploreColours = colouring },
            ColourAs);

        ViewModel.Elevation.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();
        ViewModel.Targeting.Typed.Accepted += OnTypedAccepted;
        FollowRows();

        InitializeComponent();

        // A row's share bar comes in from nothing as the row itself arrives. Never as a row only
        // scrolls into sight.
        ListAnimation.Play(RowsList, SystemMotion.Current).Arrived += BarMotion.GrowIn;

        _cards = new ExploreCards(ViewModel, Map, GrowthList, TypesList, UsedSpaceStrip);

        // Kept alive by the page's events and the replay's, which it follows for the page's life.
        _ = new ExploreReplayClock(ViewModel.Replay, this);

        _rowsMargin = RowsList.Margin;

        // The two ways a person moves this list's selection, marked as they arrive rather than
        // inferred afterwards. The guard settles as the list's containers are built again on a return
        // to the page, and ShowAs settles it as the list comes back from behind the map.
        _guard.Hear(RowsList);

        // Kept alive by the events it follows on the page, the list and the map.
        _ = new ExploreInput(this, ViewModel, Map, RowsList, RowsMenu);

        FollowSelection();
        WireMap();

        // Read once, here, and never again: see DrawnSetting.Stored.
        ViewModel.Notes.Dismissed = _notesDismissed.Stored;

        FollowAppearance();

        // A scan of a full drive takes long enough that throwing it away on a trip to Settings and
        // back would be its own defect.
        NavigationCacheMode = NavigationCacheMode.Required;

        // An elevated replacement opens straight here, and is pointed where the instance it replaced
        // was pointed once its drives have been read, as it is first shown (OnNavigatedTo).
        _resuming = ElevatedRelaunch.Requested as ExploreRequest;
    }

    /// <summary>Put the highlight back on the rows each time the view-model gives the list new ones.</summary>
    private void FollowRows()
    {
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
    }

    /// <summary>
    /// One signal, followed by both screens that show a selection. The list is not on screen while
    /// the map is and keeps whatever was highlighted in it until something says otherwise, and the
    /// map draws an outline round what was picked, so a change reaching only one of them is a Delete
    /// pointed at something the other one is still showing. Here rather than at each call site,
    /// because the callers are five and growing: a map click, a row click, a navigation, a snapshot
    /// carried over mid-scan, and the end of a removal.
    /// </summary>
    private void FollowSelection() =>
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

    /// <summary>The map's wiring every page shares (<see cref="MapWiring"/>), and what only Explore's map does.</summary>
    private void WireMap()
    {
        MapWiring.Attach(
            Map,
            ViewModel,
            new DrawnSetting<bool>(
                App.Preferences,
                current => current.ExploreOverviewHidden,
                (current, hidden) => current with { ExploreOverviewHidden = hidden },
                hidden => Map.OverviewHidden = hidden));

        Map.Removal = ViewModel.ShapeRemoval;
        Map.ContrastChanged += (_, _) => ViewModel.MapInContrast = Map.Contrasted;
        Map.ZoomingIn += (_, from) => ViewModel.Visiting.Zoomed(from);
        Map.ViewportChanged += (_, _) => ViewModel.Visiting.ViewportChanged();

        // A step away remembers the zoom it left, so Back comes back to it. See ExploreVisiting.Viewing.
        ViewModel.Visiting.Viewing = () => Map.Viewport;
    }

    /// <summary>Draw in the look, the colouring and the view the appearance holds, and follow the look as it changes.</summary>
    private void FollowAppearance()
    {
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
    ///
    /// <para>The highlight is put back even where the list keeps the rows it had: the folder's rows
    /// are not rewritten while a map is on screen, so coming back to them rewrites them, and a
    /// rebuild drops the highlight with the rows it was on.</para>
    /// </summary>
    private void ShowRowsOf(ExploreView view)
    {
        var files = view == ExploreView.Files;
        object rows = files ? ViewModel.Files.Rows : ViewModel.Listing.Rows;

        if (!ReferenceEquals(RowsList.ItemsSource, rows))
        {
            // The list reports every row it drops on the way, and none of that is the user's doing.
            _guard.Write(() =>
            {
                RowsList.ItemsSource = null;
                RowsList.SelectionMode = files ? ListViewSelectionMode.Single : ListViewSelectionMode.Extended;
                RowsList.ItemTemplate = (DataTemplate)Resources[files ? "FileRow" : "FolderRow"];
                RowsList.ItemsSource = rows;
            });
        }

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
                frame.Caption,
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

    /// <summary>Put the notes away, leaving the button that brings them back. See <see cref="DrawnSetting{T}"/>.</summary>
    private void OnDismissNotes(object sender, RoutedEventArgs e) => _notesDismissed.Choose(true);

    /// <summary>Ask for them back. The other half of <see cref="OnDismissNotes"/>.</summary>
    private void OnShowNotes(object sender, RoutedEventArgs e) => _notesDismissed.Choose(false);

    private void ShowNotesDismissed(bool dismissed)
    {
        ViewModel.Notes.Dismissed = dismissed;

        // The button that was just pressed is the one that has gone, so focus would be left on a
        // collapsed element and the reader would have to cross the whole page to reach the other.
        // §7 asks for a sentence to stay reachable "by whatever the reader is using", and a keyboard
        // is one of those. The corner keeps the focus instead, whichever way the toggle went.
        _ = (dismissed ? ShowNotes : DismissNotes).Focus(FocusState.Programmatic);
    }

    private void OnViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewSelector.SelectedIndex is not (>= 0 and var index) || index >= ExploreChoice.Views.Count)
        {
            return;
        }

        _view.Choose(ExploreChoice.Views[index]);
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

    private void OnColourSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _colouring.Choose((ExploreColouring)ColourSelector.SelectedIndex);

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

    /// <summary>
    /// The box opening starts its suggestion session, so what the network answers is kept until it
    /// closes. The list itself opens once the box has focus (<see cref="OnTypedFocused"/>).
    /// </summary>
    private void OnTypedOpened(object? sender, object e)
    {
        ViewModel.Targeting.Typed.Suggestions.Open();
        TypedBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// A check still asking a server when the box closes is stopped, so a server that answers late
    /// cannot point the page at a path the reader has walked away from. The suggestion session ends
    /// with it, and so does any lookup it was waiting on.
    /// </summary>
    private void OnTypedClosed(object? sender, object e)
    {
        if (ViewModel.Targeting.Typed.GoCommand.IsRunning)
        {
            ViewModel.Targeting.Typed.GoCancelCommand.Execute(null);
        }

        ViewModel.Targeting.Typed.Suggestions.Close();
        _highlighted = null;
    }

    /// <summary>The suggestion the arrow keys last highlighted, which Tab completes. Null once the text is typed over.</summary>
    private PathSuggestionRow? _highlighted;

    /// <summary>The box gaining focus lists what fits what it already holds: everything this account uses, while it is empty.</summary>
    private async void OnTypedFocused(object sender, RoutedEventArgs e)
    {
        TypedBox.IsSuggestionListOpen = true;
        await ViewModel.Targeting.Typed.Suggestions.SuggestAsync(TypedBox.Text);
    }

    /// <summary>
    /// Typing lists what fits. Only typing: the box also raises this as a highlighted suggestion
    /// writes its path into the text, and listing again then would replace the list being moved through.
    /// </summary>
    private async void OnTypedTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason is not AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _highlighted = null;
        await ViewModel.Targeting.Typed.Suggestions.SuggestAsync(sender.Text);
    }

    private void OnTypedSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) =>
        _highlighted = args.SelectedItem as PathSuggestionRow;

    /// <summary>
    /// Tab completes the highlighted suggestion and lists the next level: a computer's shares, or a
    /// share's folders. Without one highlighted, Tab moves on as it does anywhere else.
    /// </summary>
    private async void OnTypedPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not Windows.System.VirtualKey.Tab || _highlighted is not { } highlighted)
        {
            return;
        }

        e.Handled = true;
        await CompleteAsync(highlighted);
    }

    /// <summary>
    /// Enter, or a suggestion clicked. A folder chosen fills the box and is used, as Enter on typed
    /// text is. A computer is no folder (<see cref="NetworkPlace.IsFolder"/>),
    /// so choosing one completes it and lists its shares instead of being refused.
    /// </summary>
    private async void OnTypedSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var typed = ViewModel.Targeting.Typed;

        if (args.ChosenSuggestion is not PathSuggestionRow chosen)
        {
            await typed.UseAsync(typed.Text);
        }
        else if (chosen.Suggestion.Place.IsFolder)
        {
            await typed.UseAsync(chosen.Path);
        }
        else
        {
            await CompleteAsync(chosen);
        }
    }

    /// <summary>Write <paramref name="suggestion"/>'s next stage into the box and list what is inside it.</summary>
    private async Task CompleteAsync(PathSuggestionRow suggestion)
    {
        _highlighted = null;
        ViewModel.Targeting.Typed.Text = suggestion.Suggestion.Completion;
        TypedBox.IsSuggestionListOpen = true;
        await ViewModel.Targeting.Typed.Suggestions.SuggestAsync(suggestion.Suggestion.Completion);
    }

    /// <summary>
    /// The chevron: a tree of the network for a share whose name is not known. The box closes while
    /// the dialog is open, because WinUI shows one popup layer at a time, and opens again with the
    /// place chosen in it, checked as typed text is, so a share that is not there says so in the box.
    /// </summary>
    private async void OnBrowseNetwork(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }

        TypedFlyout.Hide();

        using var browser = new NetworkBrowser(
            NetworkAccess.Machine, TimeProvider.System, App.Preferences, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var dialog = new NetworkBrowserDialog(browser) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };

        await ModalDialog.ShowAsync(dialog);

        if (dialog.Chosen is not { } chosen)
        {
            return;
        }

        TypedFlyout.ShowAt(TypedButton);
        await ViewModel.Targeting.Typed.UseAsync(chosen);
    }

    private void OnTypedAccepted(object? sender, EventArgs e) => TypedFlyout.Hide();

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

    /// <summary>Start the scan an elevated replacement was launched to run. See the constructor.</summary>
    private void StartRequestedScan(object sender, RoutedEventArgs e)
    {
        Loaded -= StartRequestedScan;

        ViewModel.ScanCommand.TryExecute();
    }
}
