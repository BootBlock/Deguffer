using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Drives the Explore page: pick a drive or a folder, scan it, and move around what was found.
///
/// <para>This orchestrates. It holds no knowledge of how a volume is read — that is
/// <see cref="ExploreScanner"/>'s, and it chooses between §5.5's two routes on its own — none of
/// how a rectangle is drawn, which belongs to the layout and the rasteriser in Core, and none of
/// how a row is worded, which is <see cref="ExploreRowText"/>'s. What is left here is which node is
/// being looked at (G2).</para>
///
/// <para><b>Past G1's 500-line ceiling, and the reason is that what remains does not divide.</b>
/// Every seam this page had has been cut and lives beside it: the row and crumb values, the wording
/// of a row, the legend's bands, the drawing, and the scanning. What is left is one page's
/// controller, and its parts are not independently useful — the scan, the scope, the navigation and
/// the view selection all read and write the same current tree and current node, so a second type
/// over them would share that state rather than own any of it.</para>
/// </summary>
public sealed partial class ExploreViewModel : ObservableObject
{
    private readonly ExploreScanner _scanner;
    private readonly IVolumeInventory _volumes;

    /// <summary>
    /// Whether a finished scan covers what the page is pointed at now. Not <see cref="Tree"/>, which
    /// a snapshot fills in while a scan is still running (see <see cref="Report"/>) — a half-drawn
    /// map is not a scan the offer may be read from. Written only by
    /// <see cref="OfferElevation"/>.
    /// </summary>
    private bool _hasScanned;

    /// <summary>
    /// True while <see cref="RefreshDrives"/> is rebuilding the list.
    ///
    /// <para>Emptying an <c>ItemsSource</c> makes the picker write null back through its two-way
    /// binding, before this has put the selection back. Taken at face value that is the user
    /// choosing a different drive, so it drops the folder scope — and the refresh happens as the
    /// picker opens, which is to say every time somebody looks at the list without touching
    /// it.</para>
    /// </summary>
    private bool _rebuildingDrives;

    public ExploreViewModel(ExploreScanner scanner, IVolumeInventory volumes, ExploreActions actions)
    {
        _scanner = scanner;
        _volumes = volumes;

        Selection = new ExploreSelection(actions);

        // A removal and a scan of the same drive have no business overlapping, so each stands the
        // other down through the one busy flag. One direction each, rather than a flag both write:
        // the selection says when it is working, and this says when the page is free to act.
        Selection.Working += (_, working) => IsBusy = working;
        Selection.Reported += (_, sentence) => Status = sentence;
        Selection.Changed += (_, _) => Refresh();

        RefreshDrives();

        // Offered before anything has been scanned, so an elevated scan does not have to be reached
        // through the walked one it replaces.
        OfferElevation(null);
    }

    /// <summary>What the user picked out by hand, and what §7.1 lets them do with it.</summary>
    public ExploreSelection Selection { get; }

    /// <summary>The volumes offered in the picker, each with what it is called and how full it is.</summary>
    public ObservableCollection<DriveChoice> Drives { get; } = [];

    /// <summary>
    /// What the current node holds, in the tree's own order: largest first once a scan has
    /// finished, and by name while one is still running. See <see cref="ExploreChildOrder"/>.
    /// </summary>
    public ObservableCollection<ExploreRow> Rows { get; } = [];

    /// <summary>The path from the scan's root down to the current node.</summary>
    public ObservableCollection<ExploreCrumb> Trail { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ElevateAndRescanCommand))]
    public partial DriveChoice? SelectedDrive { get; set; }

    /// <summary>
    /// The folder a scan is scoped to, or null for a whole drive.
    ///
    /// <para>Set from the system picker rather than from a typed string, so the path is one the
    /// shell confirmed exists — the same reason <see cref="SettingsViewModel"/>'s source folders go
    /// through one.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScopedToFolder))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ElevateAndRescanCommand))]
    public partial string? ScopeFolder { get; set; }

    /// <summary>
    /// What the next scan covers: the chosen folder, and the whole drive where none was chosen.
    /// Choosing a folder is the more specific act, so it wins, and the drive box follows it rather
    /// than contradicting it — see <see cref="ScopeTo"/>.
    ///
    /// <para>Not bound to anything. The screen states the two halves separately, in the drive box
    /// and the folder beside it, and this is what the scan is actually pointed at.</para>
    /// </summary>
    private string? ScanRoot => ScopeFolder ?? SelectedDrive?.RootPath;

    public bool IsScopedToFolder => ScopeFolder is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ElevateAndRescanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanWholeDriveCommand))]
    public partial bool IsBusy { get; set; }

    partial void OnIsBusyChanged(bool value) => Selection.CanAct = !value;

    /// <summary>
    /// Whether the page will accept a new instruction. The folder picker is opened by the page
    /// rather than by a command here — it is a WinUI dialog needing a window handle — so it has no
    /// <c>CanExecute</c> of its own to disable it while a scan runs.
    /// </summary>
    public bool IsIdle => !IsBusy;

    /// <summary>
    /// How far through, 0 to 1, or null where the route cannot say.
    ///
    /// <para>The file table states its record count before the first read, so that route drives a
    /// real bar. A walk cannot know how many directories it has yet to open, so it gets an
    /// indeterminate one rather than a made-up denominator that would run to 90% and stop.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    [NotifyPropertyChangedFor(nameof(HasNoProgressFraction))]
    public partial double? Progress { get; set; }

    /// <summary>
    /// The same figure as a plain number, because a progress bar's value is not nullable and a
    /// binding that has to fall back is a binding that fails silently when it is wrong.
    /// </summary>
    public double ProgressValue => Progress ?? 0;

    /// <summary>Whether the bar has to be indeterminate. See <see cref="Progress"/>.</summary>
    public bool HasNoProgressFraction => Progress is null;

    [ObservableProperty]
    public partial string Status { get; set; } =
        "Choose a drive or a folder and scan it to see what is using the space.";

    /// <summary>The sentence §5.5 requires beside a walked scan, or null when the table answered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRouteNote))]
    public partial string? RouteNote { get; set; }

    public bool HasRouteNote => !string.IsNullOrEmpty(RouteNote);

    /// <summary>
    /// Whether to offer a relaunch as administrator, on the same terms the Storage page offers it:
    /// before anything is scanned, and afterwards only where the scan actually fell back for want
    /// of rights.
    ///
    /// <para>This says whether elevating would help, not whether the page is free to act on it —
    /// that is the command's own <c>CanExecute</c>.</para>
    /// </summary>
    [ObservableProperty]
    public partial bool CanElevate { get; set; }

    /// <summary>What that button says. See <see cref="ElevationOffer.Label"/>.</summary>
    public string ElevateLabel => ElevationOffer.Label(_hasScanned);

    /// <summary>
    /// The scan that is on screen. Replaced wholesale rather than mutated, so a snapshot arriving
    /// mid-scan cannot be half-applied while something is drawing from it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTree))]
    [NotifyPropertyChangedFor(nameof(HasNoTree))]
    [NotifyPropertyChangedFor(nameof(ViewNote))]
    [NotifyPropertyChangedFor(nameof(HasViewNote))]
    [NotifyPropertyChangedFor(nameof(ShowsAgeLegend))]
    [NotifyCanExecuteChangedFor(nameof(AscendCommand))]
    public partial ExploreTree? Tree { get; set; }

    /// <summary>
    /// Which picture the user asked for.
    ///
    /// <para>Here rather than only in the page, because the page is not the only thing that has to
    /// answer for it. While a scan is running the map draws the icicle whatever this says, and
    /// <see cref="ViewNote"/> is what keeps that from being a silent substitution.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewNote))]
    [NotifyPropertyChangedFor(nameof(HasViewNote))]
    [NotifyPropertyChangedFor(nameof(ShowsAgeLegend))]
    public partial ExploreView SelectedView { get; set; }

    /// <summary>
    /// What the colours on the map are to say. See <see cref="ExploreColouring"/>.
    ///
    /// <para>Unlike <see cref="SelectedView"/> this is never substituted. A partial tree is coloured
    /// exactly as a finished one is — an age is a fact about a node rather than about the ordering
    /// of its siblings — so a scan in progress needs no sentence explaining this one away.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAgeLegend))]
    public partial ExploreColouring SelectedColouring { get; set; }

    /// <summary>
    /// What each colour on the map means, or an empty list where the colours are branches.
    ///
    /// <para>A legend is not decoration for this one. A hue per branch is self-explanatory, because
    /// the branch it names is the rectangle it is inside — but an age band means nothing at all
    /// without the scale beside it, and a picture whose colours the reader cannot decode is worse
    /// than one with no colours in it.</para>
    /// </summary>
    public IReadOnlyList<ExploreLegendBand> AgeLegend => ExploreLegendBand.All;

    /// <summary>
    /// Whether to show that legend: only when the colours are ages, only when there is a picture
    /// rather than a list, and only once something has been scanned into it. A scale beside an
    /// empty card explains nothing and reads as part of the empty state.
    /// </summary>
    public bool ShowsAgeLegend =>
        SelectedColouring == ExploreColouring.Age && SelectedView != ExploreView.List && HasTree;

    /// <summary>
    /// How what is on screen differs from what the View box names, or null when it does not.
    ///
    /// <para>Only ever raised by a partial tree, and every view is affected by one. Its children are
    /// ordered by name rather than by size, so that a scan in progress stays still, and neither the
    /// treemap nor the sunburst can be drawn from that at all — squarification is defined only over
    /// a decreasing sequence, and a sunburst's residual wedge assumes the small children are the
    /// tail — so the map substitutes the icicle on top. A scan reading the file table publishes no
    /// partial tree, so it never says any of this.</para>
    ///
    /// <para>The list gets the sentence as much as the pictures do. It reorders itself alphabetically
    /// and back again, which is a substitution too, and one nobody is told about is the kind a user
    /// reads as a bug.</para>
    /// </summary>
    public string? ViewNote => Tree is { ChildOrder: not ExploreChildOrder.BySize }
        ? SelectedView switch
        {
            ExploreView.Treemap =>
                "Drawing the icicle, in name order, while the scan runs. A treemap reorders every "
                + "folder as it grows, so it follows when the scan finishes.",
            ExploreView.Sunburst =>
                "Drawing the icicle, in name order, while the scan runs. A sunburst turns every "
                + "wedge after one that grows, so it follows when the scan finishes.",
            _ =>
                "In name order while the scan runs, so nothing moves as a folder grows. Largest "
                + "first when the scan finishes.",
        }
        : null;

    public bool HasViewNote => ViewNote is not null;

    /// <summary>
    /// Which node the views are drawing. The scan's root until the user descends, and then wherever
    /// they descended to — including across the partial trees a running scan publishes, which is
    /// <see cref="ExplorePlace"/>'s job to establish.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AscendCommand))]
    public partial int CurrentNode { get; set; }

    /// <summary>What is under the pointer, or the current node when nothing is.</summary>
    [ObservableProperty]
    public partial string Hovered { get; set; } = string.Empty;

    /// <summary>
    /// How big that is, and how old, said apart from the path.
    ///
    /// <para>Two properties for one sentence, because the line they share holds one line's worth
    /// and a path is what overruns it. Written as one string, a deep path pushed the size off the
    /// end — and the size is the answer to the question the whole page exists to ask. Split, the
    /// figures take the width they need and the path trims into what is left.</para>
    /// </summary>
    [ObservableProperty]
    public partial string HoveredFigures { get; set; } = string.Empty;

    public bool HasTree => Tree is not null;

    /// <summary>
    /// Whether to show the empty state instead of a picture. A large blank card reads as a screen
    /// that has failed rather than one waiting to be told to start.
    /// </summary>
    public bool HasNoTree => Tree is null;

    /// <summary>
    /// Whether the rows are being rewritten from here.
    ///
    /// <para>Read by the page, because a bound <c>ListView</c> drops an item from its own
    /// selection when the collection under it stops holding that item where it was, and reports
    /// that back as a selection change. Taken for a gesture, it overwrites the selection with
    /// whatever the rewrite happened to leave behind — on a rescan that is a node number belonging
    /// to the tree before it, and asking the arriving tree for its path throws.</para>
    /// </summary>
    public bool IsShowingRows { get; private set; }

    /// <summary>
    /// Raised once the tree, the current node or the rows have changed, so the map redraws and the
    /// list's own selection is put back in step with <see cref="ExploreSelection.Nodes"/>. An event
    /// rather than the control watching several properties: a redraw is expensive and must happen
    /// once per change, not once per property that took part in it.
    /// </summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised once a replacement process is running and this one should stand down.</summary>
    public event EventHandler? ReplacedByElevatedInstance;

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken ct)
    {
        if (ScanRoot is not { } target)
        {
            return;
        }

        // Read once, at the start. The scope can be changed while a scan runs, and a sentence
        // written afterwards would then describe the next scan rather than this one's result.
        var what = IsScopedToFolder ? "folder" : "drive";

        IsBusy = true;
        Progress = null;
        RouteNote = null;

        // Back to what is known before anything is measured. The previous scan's fallback reason is
        // about to be replaced, and a cancelled or failed scan never reaches the offer below.
        OfferElevation(null);

        Status = $"Scanning {target}…";

        try
        {
            var scan = await _scanner.ScanAsync(target, new Progress<ExploreProgress>(Report), ct);

            Show(scan.Tree, ExplorePlace.Carry(Tree, CurrentNode, scan.Tree));

            RouteNote = scan.RouteNote;
            OfferElevation(scan.Fallback);

            Status = scan.Tree.HasUnknownSizes
                ? $"{FreeSpace.Format(scan.Tree.TotalBytes)} accounted for. Some of this {what} could not "
                  + "be read, so the totals are lower bounds."
                : $"{FreeSpace.Format(scan.Tree.TotalBytes)} accounted for.";
        }
        catch (OperationCanceledException)
        {
            // The last snapshot goes with it. A partial tree covers only the levels walked so far,
            // its HasUnknownSizes is false because nothing refused anything, and it draws and
            // navigates exactly like a finished scan — so leaving it on screen states a total for
            // the drive that is wrong by however much was left.
            Tree = null;
            Selection.Show(null);
            Rows.Clear();
            Trail.Clear();
            ViewChanged?.Invoke(this, EventArgs.Empty);

            Status = "Scan cancelled. Nothing was measured.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A volume that went away mid-scan, or one the account cannot open at all. Neither is
            // worth taking the window down for, and the page is read-only — there is nothing
            // half-done to report.
            Status = $"Could not scan {target}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Progress = null;
        }
    }

    /// <summary>
    /// Point the next scan at <paramref name="folder"/> and everything below it.
    ///
    /// <para>The picking itself belongs to the page: it is a WinUI dialog needing a window handle,
    /// and this stays testable by knowing only about the path that comes back — the arrangement
    /// <see cref="SettingsViewModel.AddSourceRoot"/> already uses for the same dialog.</para>
    /// </summary>
    public void ScopeTo(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        // The drive box moves to the volume holding the folder, where that volume is one it offers.
        // The two controls describe a single choice, and a drive box naming a volume the scan is not
        // on is the kind of disagreement a reader takes for a bug. A folder on a share, or on a
        // volume the box does not list, has no entry to move to and leaves it as it was.
        //
        // Assigned before the folder, because selecting a drive is what drops a folder scope. The
        // other order would clear the scope this is establishing.
        if (Offered(Path.GetPathRoot(folder)) is { } listed)
        {
            SelectedDrive = listed;
        }

        ScopeFolder = folder;
    }

    /// <summary>
    /// Point the page where an earlier instance was pointed, so an elevated replacement resumes the
    /// scan the user asked for instead of starting from nothing. See <see cref="ExploreRequest"/>.
    ///
    /// <para>A drive that is no longer mounted, and so is not in <see cref="Drives"/>, is left
    /// alone rather than forced into the box: the picker would then name a volume it cannot
    /// offer.</para>
    /// </summary>
    /// <returns>
    /// Whether the page is now pointed at what was asked for. False means nothing was restored —
    /// the drive has gone and no folder was named — and the caller must not scan on the strength of
    /// it. The box still holds whichever volume it defaulted to, and scanning a volume the user
    /// never chose is worse than scanning nothing.
    /// </returns>
    public bool PointAt(string? drive, string? folder)
    {
        var pointed = false;

        // The drive first, for the reason ScopeTo gives: selecting one drops any folder scope, so
        // the other order would clear the scope this is restoring.
        if (Offered(drive) is { } mounted)
        {
            SelectedDrive = mounted;
            pointed = true;
        }

        if (folder is not null)
        {
            ScopeFolder = folder;
            pointed = true;
        }

        return pointed;
    }

    /// <summary>
    /// The entry in <see cref="Drives"/> naming <paramref name="volume"/>, or null where the box
    /// does not offer it. Null in, null out, so a path with no root and an absent drive are one case
    /// here rather than two at each caller.
    /// </summary>
    private DriveChoice? Offered(string? volume) =>
        volume is null
            ? null
            : Drives.FirstOrDefault(listed => listed.RootPath.Equals(volume, StringComparison.OrdinalIgnoreCase));

    /// <summary>Drop a folder scope, so the next scan covers the whole drive again.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ScanWholeDrive() => ScopeFolder = null;

    /// <summary>
    /// Choosing a drive is choosing to scan the whole of it, so any folder scope goes with it. The
    /// alternative leaves both set, and the page then states one target while scanning another.
    ///
    /// <para>Only where a person chose it. See <see cref="_rebuildingDrives"/> for the writes that
    /// come from rebuilding the list rather than from the picker.</para>
    /// </summary>
    partial void OnSelectedDriveChanged(DriveChoice? value)
    {
        if (_rebuildingDrives)
        {
            return;
        }

        ScopeFolder = null;

        // Called here as well as from the scope's own handler below. Assigning null over null
        // raises nothing, so a drive chosen while no folder was scoped would otherwise leave the
        // offer describing somewhere the page is no longer pointed.
        OfferElevation(null);
    }

    /// <summary>
    /// The offer describes what pressing the button would scan, not what is drawn on screen, so
    /// moving the target puts it back to the state before anything was measured.
    ///
    /// <para>Leaving it alone is how the button comes to be hidden for a volume nothing has looked
    /// at, which is the whole of the defect it was just changed to fix, and how it comes to offer a
    /// rescan of a drive that was never scanned.</para>
    /// </summary>
    partial void OnScopeFolderChanged(string? value) => OfferElevation(null);

    /// <summary>
    /// §6.3: a process cannot grant itself rights it started without, so this starts a replacement
    /// and stands down — the same mechanism the Storage page uses, and for the same reason.
    ///
    /// <para>The replacement is told where this page was pointed, so it opens on Explore and scans
    /// it. The user pressed this while pointed somewhere, and landing them on another page with the
    /// drive box back at its default, and a picked folder thrown away, would not be that.</para>
    ///
    /// <para>What travels is what the page is pointed at now rather than what the last scan
    /// covered. The picker is on screen and <see cref="ScanCommand"/> beside it would use exactly
    /// these two values, so a second, hidden idea of the target is one the page could then
    /// contradict — and it is why this shares that command's <c>CanExecute</c> rather than only
    /// asking whether the page is busy. A relaunch with nothing to point at leaves the replacement
    /// waiting on a page the user did not ask to be on.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private void ElevateAndRescan()
    {
        if (!ElevatedRelaunch.TryRelaunch(new ExploreRequest(SelectedDrive?.RootPath, ScopeFolder)))
        {
            Status = "Deguffer is still running without administrator rights, so it scans by walking "
                + "directories. Everything else works exactly the same.";
            return;
        }

        ReplacedByElevatedInstance?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Say whether elevating would help, from whatever is known at the point of asking.
    ///
    /// <para>One place, because the two halves of the answer are one fact: what the button offers
    /// and what it says both turn on whether a scan has finished, and setting them apart is how a
    /// button comes to offer a rescan of a scan that never ran.</para>
    /// </summary>
    /// <param name="found">
    /// Why the finished scan walked, or null where no scan has finished — which is the state before
    /// the first one, and the state a cancelled or failed one returns the page to.
    /// </param>
    private void OfferElevation(FallbackReason? found)
    {
        _hasScanned = found is not null;

        CanElevate = found is { } fallback
            ? ElevationOffer.ShouldOffer(ElevatedRelaunch.IsElevated, fallback)
            : ElevationOffer.ShouldOffer(ElevatedRelaunch.IsElevated);

        OnPropertyChanged(nameof(ElevateLabel));
    }

    /// <summary>Show what is inside <paramref name="node"/>. Ignored for anything with nothing in it.</summary>
    public void Descend(int node)
    {
        if (Tree is not { } tree || Selection.WasRemoved(node) || !tree.IsDirectory(node)
            || tree.ChildrenOf(node).Length == 0)
        {
            return;
        }

        Show(tree, node);
    }

    [RelayCommand(CanExecute = nameof(CanAscend))]
    private void Ascend()
    {
        if (Tree is { } tree && CurrentNode != tree.RootNode)
        {
            Show(tree, tree.ParentOf(CurrentNode));
        }
    }

    /// <summary>Jump straight to a node on the trail.</summary>
    public void GoTo(int node)
    {
        if (Tree is { } tree)
        {
            Show(tree, node);
        }
    }

    /// <summary>
    /// Say what the pointer is over. Called from the map on every move, so it formats and assigns
    /// and does nothing else — anything heavier here runs at the display's refresh rate.
    /// </summary>
    public void Hover(int? node, long? aggregateBytes)
    {
        (Hovered, HoveredFigures) = (Tree, node, aggregateBytes) switch
        {
            (_, _, { } bytes) => ("Items too small to draw separately", FreeSpace.Format(bytes)),

            // The age is on this line whichever colouring is on, not only when the map is drawn by
            // age. A pointer is how somebody checks one shape against the rest, and having to change
            // the colouring to read a date would be a worse answer than showing it always.
            ({ } tree, { } value, _) => (
                tree.PathOf(value),
                $"{FreeSpace.Format(tree.SizeOf(value))}, "
                + $"last written {ExploreRowText.Age(tree, value, DateTime.UtcNow)}"),

            _ => (string.Empty, string.Empty),
        };
    }

    private void Report(ExploreProgress progress)
    {
        Progress = progress.Fraction;

        Status = progress.Total is null
            ? $"Scanning… {progress.Done:N0} items, {FreeSpace.Format(progress.BytesSeen)} so far"
            : $"Reading the file table… {progress.Fraction:P0}";

        // A partial tree, on the cadence the scanner chose. Drawing it is what stops a long scan
        // looking like a hung window — and it is only ever a snapshot, so the finished tree replaces
        // it rather than being merged into it.
        if (progress.Snapshot is { } snapshot)
        {
            Show(snapshot, ExplorePlace.Carry(Tree, CurrentNode, snapshot));
        }
    }

    /// <summary>
    /// Point every view at one node of one tree, in one place.
    ///
    /// <para>The rows, the trail and the map all describe the same thing, so they are rebuilt
    /// together. Updating them from separate handlers is how a breadcrumb comes to name a directory
    /// the list below it is no longer showing.</para>
    /// </summary>
    private void Show(ExploreTree tree, int node)
    {
        var standing = Tree;

        // Whether what is on screen is still about the same directory. A snapshot landing mid-scan
        // is: it measures again what the page is already standing in. Stepping into a folder is
        // not, and neither is a scan that came back rooted somewhere else.
        var continuing = ExplorePlace.TryCarry(standing, CurrentNode, tree) == node;

        Tree = tree;
        CurrentNode = node;

        if (continuing)
        {
            Selection.Carry(tree);
        }
        else
        {
            Selection.Show(tree);
        }

        // Brought up to date in place only while the list is the same list: the same directory, in
        // the same order. A finished tree arrives with its children in size order after a walk's
        // snapshots delivered them in name order, and that is a different list rather than this one
        // changed — every row has moved, so reconciling it would be a move per entry, and the scroll
        // position it would preserve is a position in content that is no longer there.
        ShowRows(tree, node, continuing && standing?.ChildOrder == tree.ChildOrder);
        BuildTrail(tree, node);

        Hovered = string.Empty;
        HoveredFigures = string.Empty;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rebuild the list and the picture from what is left, without rescanning.</summary>
    private void Refresh()
    {
        if (Tree is { } tree)
        {
            // The same directory of the same tree, with something taken out of it, so the rows are
            // brought up to date rather than rebuilt and the list stays where the user left it.
            ShowRows(tree, CurrentNode, reconcile: true);
        }

        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Bring <see cref="Rows"/> to what <paramref name="node"/> holds in <paramref name="tree"/>.
    /// </summary>
    /// <param name="reconcile">
    /// Whether the rows already there are about this same directory.
    ///
    /// <para>Clearing the collection is a Reset for the bound <c>ListView</c>, and a Reset throws
    /// away the scroll position and the selection with it — which, on a walked scan publishing a
    /// snapshot every few hundred milliseconds, made reading a long list or picking a folder out of
    /// one impossible while the scan ran. So a list still about the same thing is brought up to
    /// date in place: the rows that stay are the same objects, and the <c>ListView</c> is told
    /// nothing it has to recover from.</para>
    ///
    /// <para>A list about something else is rebuilt. The identities are unrelated, so matching them
    /// up would keep a highlight the user put on a different folder, and the scroll position is
    /// about content that is no longer there.</para>
    /// </summary>
    private void ShowRows(ExploreTree tree, int node, bool reconcile)
    {
        IsShowingRows = true;

        try
        {
            Rewrite(tree, node, reconcile);
        }
        finally
        {
            IsShowingRows = false;
        }
    }

    /// <summary>The pass itself. See <see cref="ShowRows"/>, which is what holds the gate open.</summary>
    private void Rewrite(ExploreTree tree, int node, bool reconcile)
    {
        if (!reconcile)
        {
            Rows.Clear();
        }

        var total = tree.SizeOf(node);

        // Once for the whole list rather than once per row. Every age in it is measured against the
        // same instant, which is both cheaper and the only way two rows a millisecond apart cannot
        // land in different days (G5).
        var now = DateTime.UtcNow;

        var at = 0;

        foreach (var child in tree.ChildrenOf(node))
        {
            if (Selection.WasRemoved(child))
            {
                continue;
            }

            if (at < Rows.Count && Rows[at].Is(tree, child))
            {
                Rows[at].Describe(tree, total, now);
            }
            else if (RowFor(tree, child, at) is { } sits)
            {
                Rows.Move(sits, at);
                Rows[at].Describe(tree, total, now);
            }
            else
            {
                Rows.Insert(at, new ExploreRow(tree, child, total, now));
            }

            at++;
        }

        // Whatever the pass above did not claim: rows for things that have been removed, and the
        // tail left behind when a directory holds fewer entries than it did.
        while (Rows.Count > at)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }

    /// <summary>
    /// Where the row for <paramref name="child"/> sits at or after <paramref name="from"/>, or null
    /// where none of them is about it. Everything before <paramref name="from"/> is already settled,
    /// so the search starts there.
    ///
    /// <para>The search starts where the row would be if nothing had moved, so what the pass around
    /// it costs is how far the rows actually travelled: nothing at all while the sequence is
    /// unchanged, which is every snapshot of one walk, and one step each after an entry is removed
    /// from the directory on screen. It is quadratic only where most of the list has moved, and
    /// <see cref="Show"/> keeps the one case that guarantees that — a finished tree in size order
    /// replacing snapshots in name order — out of here entirely. What is left is two scans of one
    /// volume, which agree on the order they are in and disagree only about whatever changed size
    /// between them (G4).</para>
    /// </summary>
    private int? RowFor(ExploreTree tree, int child, int from)
    {
        for (var i = from; i < Rows.Count; i++)
        {
            if (Rows[i].Is(tree, child))
            {
                return i;
            }
        }

        return null;
    }

    private void BuildTrail(ExploreTree tree, int node)
    {
        Trail.Clear();

        var steps = new List<ExploreCrumb>();

        for (var current = node; ; current = tree.ParentOf(current))
        {
            steps.Add(new ExploreCrumb(current, tree.NameOf(current)));

            if (current == tree.RootNode)
            {
                break;
            }
        }

        steps.Reverse();

        foreach (var step in steps)
        {
            Trail.Add(step);
        }
    }

    /// <summary>
    /// Read the volumes again, so the picker offers what is mounted now and states how full it is
    /// now.
    ///
    /// <para>Called whenever the picker is about to be read rather than once at startup. A mount
    /// point does not go stale, but the space figures beside it do: a build, a download or a
    /// removal through this very page moves them, and a picker still quoting the figures from when
    /// the app opened is worse than one quoting none.</para>
    ///
    /// <para>The chosen volume survives the rebuild. It is found again by mount point, because the
    /// entry that replaces it carries newer figures and so is not the same value.</para>
    /// </summary>
    public void RefreshDrives()
    {
        var chosen = SelectedDrive?.RootPath;

        _rebuildingDrives = true;

        try
        {
            _volumes.Invalidate();

            Drives.Clear();

            // Only volumes that can actually be read. An optical drive with no disc and a card
            // reader with no card are both mounted and both answer no, and offering them is
            // offering a scan that cannot start.
            foreach (var volume in _volumes.Volumes.Where(v => v.IsReady && v.Kind != DriveType.Network))
            {
                Drives.Add(DriveChoice.From(volume));
            }

            SelectedDrive = Offered(chosen) ?? Drives.FirstOrDefault();
        }
        finally
        {
            _rebuildingDrives = false;
        }

        // A rebuild that hands the same volume back is not a choice, and the guard above stopped it
        // reading as one. A rebuild that cannot — the drive was unplugged, the disc ejected, the
        // volume re-locked — has moved the page to a volume nobody picked, and that is a change of
        // drive however it came about. Leaving the scope and the offer alone there is exactly the
        // state OnSelectedDriveChanged exists to prevent: one target stated, another scanned.
        if (chosen is not null
            && !string.Equals(chosen, SelectedDrive?.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            ScopeFolder = null;

            // Both, for the reason OnSelectedDriveChanged gives: assigning null over null raises
            // nothing, so the scope's own handler cannot be relied on to have run.
            OfferElevation(null);
        }
    }

    private bool CanScan() => !IsBusy && ScanRoot is not null;

    private bool CanRun() => !IsBusy;

    private bool CanAscend() => Tree is { } tree && CurrentNode != tree.RootNode;
}
