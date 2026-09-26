using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

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
/// of a row, the legend's bands, the drawing, the scanning, and the rules in Core of where a step
/// leads (<see cref="ExplorePosition"/>), what a redraw keeps (<see cref="ExploreRedraw"/>), and
/// where each way of re-pointing the page leaves it (<see cref="ExploreTarget"/>). What is left is
/// one page's controller, and its parts are not independently useful — the scan, the scope, the
/// navigation and the view selection all read and write the same current tree and current node, so
/// a second type over them would share that state rather than own any of it.</para>
/// </summary>
public sealed partial class ExploreViewModel : ObservableObject
{
    private readonly IExploreScanner _scanner;
    private readonly IVolumeInventory _volumes;

    /// <summary>Whether this process holds administrator rights, which decides the elevation offer.</summary>
    private readonly bool _isElevated;

    /// <summary>
    /// Starts an elevated replacement pointed where this page is, and says whether one started. A
    /// real relaunch raises the UAC prompt, which is why it is handed in.
    /// </summary>
    private readonly Func<ExploreRequest, bool> _relaunch;

    /// <summary>
    /// What a redraw puts in the list and the trail above it. Filled again per redraw and never
    /// replaced: a walked scan publishes a snapshot every few hundred milliseconds, and building two
    /// lists for each of those is work to say what was already said (G5).
    /// </summary>
    private readonly List<int> _arriving = [];

    private readonly List<ExploreCrumb> _trail = [];

    private readonly List<ExplorePosition> _steps = [];

    private readonly DriveList _drives;

    /// <summary>Where the views are: see <see cref="ExplorePosition"/>. Written only by <see cref="Show"/>.</summary>
    private ExplorePosition _position;

    /// <summary>
    /// What the app knows about well-known files and folders, resolved against this machine once
    /// and read from here on (G5). Held rather than reached for statically, so a test that hands the
    /// page a synthetic profile gets a page that explains that profile's contents.
    /// </summary>
    private readonly ItemGuide _guide;

    /// <summary>
    /// Whether a finished scan covers what the page is pointed at now. Not <see cref="Tree"/>, which
    /// a snapshot fills in while a scan is still running (see <see cref="Report"/>) — a half-drawn
    /// map is not a scan the offer may be read from. Written only by
    /// <see cref="OfferElevation"/>.
    /// </summary>
    private bool _hasScanned;

    /// <summary>
    /// True while the page is writing its own target, so a write that arrives as a change of drive
    /// is not taken for a person choosing one.
    ///
    /// <para>Two writers are not people. <see cref="Retarget"/> writes the drive and the folder one
    /// after the other, and the drive's handler would otherwise drop the folder it is about to set.
    /// And a picker whose selected entry stops being in the list where it was writes null back
    /// through its two-way binding while <see cref="RefreshDrives"/> is reading the volumes, before
    /// this has put the selection back. Taken at face value that is the user choosing a different
    /// drive, so it drops the folder scope — and the refresh can happen as the picker opens, when
    /// somebody is only looking at the list. Rows are written over in place, so only a reading that
    /// finds the chosen drive gone still does this.</para>
    /// </summary>
    private bool _retargeting;

    /// <summary>
    /// The refusal <see cref="ExplainRefusal"/> last put on the status line, so it can take that
    /// sentence back and leave anything written over it alone.
    /// </summary>
    private string? _statedRefusal;

    /// <param name="time">What decides when the drive picker's last reading has gone stale.</param>
    /// <param name="isElevated">Whether this process holds administrator rights.</param>
    /// <param name="relaunch">See <see cref="_relaunch"/>.</param>
    public ExploreViewModel(
        IExploreScanner scanner,
        IVolumeInventory volumes,
        TimeProvider time,
        ExploreActions actions,
        ItemGuide guide,
        bool isElevated,
        Func<ExploreRequest, bool> relaunch)
    {
        _scanner = scanner;
        _volumes = volumes;
        _drives = new DriveList(volumes, time);
        _guide = guide;
        _isElevated = isElevated;
        _relaunch = relaunch;

        Selection = new ExploreSelection(actions);

        // A removal and a scan of the same drive have no business overlapping, so each stands the
        // other down through the one busy flag. One direction each, rather than a flag both write:
        // the selection says when it is working, and this says when the page is free to act.
        Selection.Working += (_, working) => IsBusy = working;
        Selection.Reported += (_, sentence) => Status = sentence;
        Selection.Changed += (_, _) => Refresh();

        // Two of the four notes are the selection's, so what the card shows in their corner turns
        // partly on a type this one does not speak for. Without following it, picking something
        // Explore will not remove while the notes are collapsed would leave no button offering to
        // say why (§7.1).
        Selection.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName is nameof(ExploreSelection.HasNote)
                or nameof(ExploreSelection.HasStaleNote))
            {
                NotesChanged();
            }
        };

        RefreshDrives();

        // Offered before anything has been scanned, so an elevated scan does not have to be reached
        // through the walked one it replaces.
        OfferElevation(null);
    }

    /// <summary>What the user picked out by hand, and what §7.1 lets them do with it.</summary>
    public ExploreSelection Selection { get; }

    /// <summary>The volumes offered in the picker, each with what it is called and how full it is.</summary>
    public ObservableCollection<DriveEntry> Drives => _drives.Entries;

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
    public partial DriveEntry? SelectedDrive { get; set; }

    /// <summary>
    /// The folder a scan is scoped to, or null for a whole drive.
    ///
    /// <para>Set from the system picker rather than from a typed string, so the path is one the
    /// shell confirmed exists — the same reason <see cref="SettingsViewModel"/>'s source folders go
    /// through one.</para>
    ///
    /// <para>Written only through <see cref="Retarget"/>, which is what keeps the drive box, the
    /// elevation offer and the status line answering for it.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScopedToFolder))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ElevateAndRescanCommand))]
    public partial string? ScopeFolder { get; private set; }

    /// <summary>
    /// What the page is pointed at, as the two controls state it. See <see cref="ExploreTarget"/>
    /// for what the next scan covers and why it may be refused.
    ///
    /// <para>Not bound to anything. The screen states the two halves separately, in the drive box
    /// and the folder beside it, and this is what the scan is actually pointed at.</para>
    /// </summary>
    private ExploreTarget Target => new(SelectedDrive?.RootPath, ScopeFolder);

    public bool IsScopedToFolder => Target.IsScopedToFolder;

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

    /// <summary>
    /// What the status line says before anything has been measured, and what it goes back to when
    /// a refusal is taken off it. See <see cref="ExplainRefusal"/>.
    /// </summary>
    private const string ScanPrompt = "Choose a drive or a folder and scan it to see what is using the space.";

    [ObservableProperty]
    public partial string Status { get; set; } = ScanPrompt;

    /// <summary>The sentence §5.5 requires beside a walked scan, or null when the table answered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRouteNote))]
    [NotifyPropertyChangedFor(nameof(ShowsNotes))]
    [NotifyPropertyChangedFor(nameof(ShowsNotesButton))]
    public partial string? RouteNote { get; set; }

    public bool HasRouteNote => !string.IsNullOrEmpty(RouteNote);

    /// <summary>
    /// Whether the reader has collapsed the notes into their button.
    ///
    /// <para>Theirs to set and nobody else's. Nothing in a scan, a selection or a removal writes
    /// it, so a note arriving afterwards does not put the panel back over the picture — it brings
    /// the button back, which is where the sentence then is.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNotes))]
    [NotifyPropertyChangedFor(nameof(ShowsNotesButton))]
    public partial bool NotesDismissed { get; set; }

    /// <summary>
    /// What the collapsed button shows, and what it is called.
    ///
    /// <para>Two states rather than one, because one of the four notes is a warning and the other
    /// three are not. §7.1 lets Explore's totals be lower bounds "provided the picture states which
    /// it is", and after a removal the stale note is the whole of that statement: the sizes on
    /// screen still count what has gone. Collapsed behind a fixed <c>i</c> it would arrive with
    /// nothing on screen changing, because the route note usually has the button on screen already.
    /// So the glyph and the name change with it, and neither is colour alone.</para>
    /// </summary>
    public string NotesButtonGlyph => Selection.HasStaleNote ? WarningGlyph : InfoGlyph;

    /// <inheritdoc cref="NotesButtonGlyph"/>
    public string NotesButtonLabel => Selection.HasStaleNote
        ? "Show the notes about this scan. One of them is a warning."
        : "Show the notes about this scan";

    /// <summary>
    /// Whether the notes are on the card. See <see cref="ShowsNotesButton"/> for the other half.
    /// </summary>
    public bool ShowsNotes => HasNotes && !NotesDismissed;

    /// <summary>
    /// Whether the button that brings them back is on the card.
    ///
    /// <para>The two are exclusive, and both are false while there is nothing to read. That is what
    /// makes the button itself the signal: it is in the notes' corner exactly when a sentence is
    /// waiting behind it, so a collapsed panel with something to say never looks like a collapsed
    /// panel with nothing.</para>
    /// </summary>
    public bool ShowsNotesButton => HasNotes && NotesDismissed;

    /// <summary>Whether any of the four notes has something to say.</summary>
    private bool HasNotes =>
        Selection.HasNote || Selection.HasStaleNote || HasViewNote || HasRouteNote;

    // Segoe Fluent Icons, by code point rather than pasted: a private-use glyph reads back as
    // nothing at all through a file tool, so the literal is the one form that can be checked.
    private const string InfoGlyph = "\uE946";
    private const string WarningGlyph = "\uE7BA";

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
    [NotifyPropertyChangedFor(nameof(ShowsMapControls))]
    [NotifyPropertyChangedFor(nameof(ShowsAgeLegend))]
    [NotifyPropertyChangedFor(nameof(ShowsNotes))]
    [NotifyPropertyChangedFor(nameof(ShowsNotesButton))]
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
    [NotifyPropertyChangedFor(nameof(ShowsMapControls))]
    [NotifyPropertyChangedFor(nameof(ShowsAgeLegend))]
    [NotifyPropertyChangedFor(nameof(ShowsNotes))]
    [NotifyPropertyChangedFor(nameof(ShowsNotesButton))]
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
    /// Which set of colours the picture on screen is drawn in. See <see cref="ExploreScheme"/>.
    /// Here rather than only on the page because the legend below has to be drawn in it too: a
    /// legend in one set of colours beside a map in another names every band wrongly.
    /// </summary>
    [ObservableProperty]
    public partial ExploreScheme SelectedScheme { get; set; }

    partial void OnSelectedSchemeChanged(ExploreScheme value) =>
        LiveList.Rewrite(AgeLegend, ExploreLegendBand.For(value));

    /// <summary>
    /// What each colour on the map means, or an empty list where the colours are branches.
    ///
    /// <para>A legend is not decoration for this one. A hue per branch is self-explanatory, because
    /// the branch it names is the rectangle it is inside — but an age band means nothing at all
    /// without the scale beside it, and a picture whose colours the reader cannot decode is worse
    /// than one with no colours in it.</para>
    /// </summary>
    public ObservableCollection<ExploreLegendBand> AgeLegend { get; } = [.. ExploreLegendBand.For(ExploreScheme.Standard)];

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
    public string? ViewNote => Tree is { ChildOrder: not ExploreChildOrder.BySize } tree
        ? SelectedView switch
        {
            ExploreView.Treemap when ExploreSurface.Drawn(tree, ExploreView.Treemap) == ExploreView.Icicle =>
                "Drawing the icicle, in name order, while the scan runs. A treemap reorders every "
                + "folder as it grows, so it follows when the scan finishes.",
            ExploreView.Sunburst when ExploreSurface.Drawn(tree, ExploreView.Sunburst) == ExploreView.Icicle =>
                "Drawing the icicle, in name order, while the scan runs. A sunburst turns every "
                + "wedge after one that grows, so it follows when the scan finishes.",
            _ =>
                "In name order while the scan runs, so nothing moves as a folder grows. Largest "
                + "first when the scan finishes.",
        }
        : null;

    public bool HasViewNote => ViewNote is not null;

    /// <summary>
    /// Whether the picture on screen is a treemap, the one the mouse zooms and drags, so the line
    /// naming how is worth showing. Not while a scan runs: the map draws the icicle then, whatever
    /// the View box says (see <see cref="ViewNote"/>).
    /// </summary>
    public bool ShowsMapControls =>
        SelectedView == ExploreView.Treemap && Tree is { } tree
        && ExploreSurface.Drawn(tree, ExploreView.Treemap) == ExploreView.Treemap;

    /// <summary>
    /// How large the volume was and how much of it was free when the scan on screen finished, where
    /// it covered the whole of one, and <see cref="VolumeSpace.None"/> otherwise.
    ///
    /// <para>Read once, as the scan finishes, rather than kept current. Everything else on screen
    /// describes the disk as that scan found it, so a figure that moved on its own would set the
    /// blocks against sizes they no longer match. A removal from this page makes both stale
    /// together, and the stale note says so.</para>
    ///
    /// <para>Replaced with every tree, because it belongs to the tree it describes: the snapshots a
    /// running scan publishes have none, being partial, so a scan of a folder never runs with the
    /// last drive's figures beside it, nor offers a way out onto that drive.</para>
    /// </summary>
    public VolumeSpace Volume { get; private set; } = VolumeSpace.None;

    /// <summary>
    /// The volume to draw beside what is on screen: <see cref="Volume"/> on the volume itself, and
    /// nothing inside its root. See <see cref="ExplorePosition.Beside"/>.
    /// </summary>
    public VolumeSpace VolumeBeside => Tree is { } tree ? _position.Beside(tree, Volume) : VolumeSpace.None;

    /// <summary>
    /// Which node the views are drawing. The scan's root until the user descends, and then wherever
    /// they descended to — including across the partial trees a running scan publishes, which is
    /// <see cref="ExplorePlace"/>'s job to establish.
    /// </summary>
    public int CurrentNode => _position.Node;

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

    /// <summary>
    /// What Deguffer knows about the thing under the pointer, or about the nearest folder above it
    /// that it knows anything about, ready to show. Empty where nothing on the way to the top of
    /// the volume is described.
    ///
    /// <para>Nearest rather than exact, because a treemap draws a folder as a frame round its
    /// children and the pointer is nearly always on a file inside it. Asked exactly, the whole of
    /// <c>C:\Windows</c> answered nothing but that frame.</para>
    ///
    /// <para>Only what the reference says, and not the size or the date: those are already on the
    /// status line under the picture, where they can be read without waiting for anything to
    /// appear. This is the part that has nowhere else to go.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHoveredNote))]
    public partial string HoveredNote { get; set; } = string.Empty;

    /// <summary>
    /// Whether there is anything to say about what the pointer is over. The map's tooltip is
    /// collapsed by this, so nothing appears where nothing on the way to the top of the volume is
    /// described — which is most of a data drive and little of a system one.
    /// </summary>
    public bool HasHoveredNote => HoveredNote.Length > 0;

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
        if (Target.Root is not { } target)
        {
            return;
        }

        // Read once, at the start. The scope can be changed while a scan runs, and a sentence
        // written afterwards would then describe the next scan rather than this one's result.
        var what = IsScopedToFolder ? "folder" : "drive";

        IsBusy = true;
        Progress = null;
        RouteNote = null;

        // Started with the scan and never awaited here. Part of §7.1's refusal set says what is
        // running right now, so it goes stale while the page is open, and a scan is the moment the
        // rest of the page is being re-measured anyway. It finishes long before a scan does.
        Selection.Reconsider();

        // Back to what is known before anything is measured. The previous scan's fallback reason is
        // about to be replaced, and a cancelled or failed scan never reaches the offer below.
        OfferElevation(null);

        Status = $"Scanning {target}…";

        try
        {
            var scan = await _scanner.ScanAsync(target, new Progress<ExploreProgress>(Report), ct);

            // Read afresh and handed to Show, which is what draws the map, because the space figures
            // the drive picker holds are from whenever it last opened.
            _volumes.Invalidate();

            Show(scan.Tree, _position.CarriedTo(Tree, scan.Tree), VolumeSpace.Of(_volumes, target));

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
            Volume = VolumeSpace.None;
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

        Retarget(Target, Target.ScopedTo(folder, _drives.Holding(folder)?.RootPath));
    }

    /// <summary>
    /// Point the page where an earlier instance was pointed, so an elevated replacement resumes the
    /// scan the user asked for instead of starting from nothing. See <see cref="ExploreRequest"/>
    /// and <see cref="ExploreTarget.Pointing"/>.
    /// </summary>
    /// <returns>
    /// Whether the page is now pointed at what was asked for. False means nothing was restored, and
    /// the caller must not scan on the strength of it.
    /// </returns>
    public bool PointAt(string? drive, string? folder)
    {
        if (Target.Pointing(_drives.Find(drive)?.RootPath, folder) is not { } pointed)
        {
            return false;
        }

        Retarget(Target, pointed);

        return true;
    }

    /// <summary>Drop a folder scope, so the next scan covers the whole drive again.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ScanWholeDrive() => Retarget(Target, Target.WholeDrive());

    /// <summary>
    /// A drive chosen in the picker, which drops any folder scope: see
    /// <see cref="ExploreTarget.Choosing"/>.
    ///
    /// <para>Only where a person chose it. See <see cref="_retargeting"/> for the writes that come
    /// from the page itself.</para>
    /// </summary>
    partial void OnSelectedDriveChanged(DriveEntry? oldValue, DriveEntry? newValue)
    {
        if (_retargeting)
        {
            return;
        }

        var before = new ExploreTarget(oldValue?.RootPath, ScopeFolder);

        Retarget(before, before.Choosing(newValue?.RootPath));
    }

    /// <summary>
    /// Point the page at <paramref name="to"/>, and make what it says about the target answer for
    /// where it now points.
    ///
    /// <para>Both halves are written under <see cref="_retargeting"/>, because the drive's handler
    /// would otherwise read the half-written pair as a person choosing a drive, and drop the folder
    /// this is setting.</para>
    ///
    /// <para>The elevation offer describes what pressing the button would scan, not what is drawn on
    /// screen, so a move that changes what the next scan covers puts it back to the state before
    /// anything was measured. Leaving it alone is how the button comes to be hidden for a volume
    /// nothing has looked at, and how it comes to offer a rescan of a drive that was never scanned.
    /// A move that scans the same place leaves it, so a reading of the volumes that hands the chosen
    /// drive back does not take away an offer the last scan made.</para>
    /// </summary>
    /// <param name="from">
    /// Where the page was pointed before the move. Passed rather than read, because a drive chosen
    /// in the picker has already been written by the time its handler runs.
    /// </param>
    private void Retarget(ExploreTarget from, ExploreTarget to)
    {
        _retargeting = true;

        try
        {
            SelectedDrive = _drives.Find(to.Drive);
            ScopeFolder = to.Folder;
        }
        finally
        {
            _retargeting = false;
        }

        if (!from.ScansTheSameAs(to))
        {
            OfferElevation(null);
        }

        ExplainRefusal();
    }

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
        if (!_relaunch(new ExploreRequest(Target.Drive, Target.Folder)))
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
            ? ElevationOffer.ShouldOffer(_isElevated, fallback)
            : ElevationOffer.ShouldOffer(_isElevated);

        OnPropertyChanged(nameof(ElevateLabel));
    }

    /// <summary>
    /// Show what is inside <paramref name="node"/>. Ignored for anything removed since the scan,
    /// which the map still draws, and wherever <see cref="ExplorePosition.Opening"/> says the step
    /// leads nowhere.
    /// </summary>
    public void Descend(int node)
    {
        if (Tree is not { } tree || Selection.WasRemoved(node)
            || _position.Opening(tree, node, Volume) is not { } opened)
        {
            return;
        }

        Show(tree, opened, Volume);
    }

    [RelayCommand(CanExecute = nameof(CanAscend))]
    private void Ascend()
    {
        if (Tree is { } tree && _position.Up(tree, Volume) is { } up)
        {
            Show(tree, up, Volume);
        }
    }

    /// <summary>Go straight to a step on the trail.</summary>
    public void GoTo(ExplorePosition position)
    {
        if (Tree is { } tree)
        {
            Show(tree, position, Volume);
        }
    }

    /// <summary>
    /// Say what the pointer is over. Called from the map on every move, so it formats and assigns
    /// and does nothing else — anything heavier here runs at the display's refresh rate.
    /// </summary>
    public void Hover(ExploreHit? hit)
    {
        (Hovered, HoveredFigures, HoveredNote) = (Tree, hit) switch
        {
            (_, { IsAggregate: true } aggregate) => (
                "Items too small to draw separately", FreeSpace.Format(aggregate.Bytes), string.Empty),

            (_, { IsFreeSpace: true } free) => (
                "Free space available on this drive", FreeSpace.Format(free.Bytes), string.Empty),

            (_, { IsUnaccounted: true } unaccounted) => (
                "In use, but not accounted for by this scan",
                FreeSpace.Format(unaccounted.Bytes),
                ExploreUnaccountedNote.For(_isElevated)),

            ({ } tree, { IsNode: true } node) => Over(tree, node.Node),

            _ => (string.Empty, string.Empty, string.Empty),
        };
    }

    /// <summary>
    /// The three things a pointer settling on one shape says: where it is, how big and how old it
    /// is, and what it is.
    ///
    /// <para>The age is here whichever colouring is on, not only when the map is drawn by age. A
    /// pointer is how somebody checks one shape against the rest, and having to change the
    /// colouring to read a date would be a worse answer than showing it always.</para>
    ///
    /// <para>The path is built once and the reference is asked with it, rather than each walking
    /// the tree's parent chain for itself. This runs on every pointer move that lands on a
    /// different shape (G4).</para>
    /// </summary>
    private (string Path, string Figures, string Note) Over(ExploreTree tree, int node)
    {
        var path = tree.PathOf(node);

        return (
            path,
            $"{FreeSpace.Format(tree.SizeOf(node))}, "
            + $"last written {ExploreRowText.Age(tree, node, DateTime.UtcNow)}",
            _guide.DescribeNearest(path)?.Tip() ?? string.Empty);
    }

    /// <summary>Say that what the notes hold has changed, whichever of the four it was.</summary>
    private void NotesChanged()
    {
        OnPropertyChanged(nameof(ShowsNotes));
        OnPropertyChanged(nameof(ShowsNotesButton));
        OnPropertyChanged(nameof(NotesButtonGlyph));
        OnPropertyChanged(nameof(NotesButtonLabel));
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
            Show(snapshot, _position.CarriedTo(Tree, snapshot), VolumeSpace.None);
        }
    }

    /// <summary>
    /// Point every view at one node of one tree, in one place.
    ///
    /// <para>The rows, the trail and the map all describe the same thing, so they are rebuilt
    /// together. Updating them from separate handlers is how a breadcrumb comes to name a directory
    /// the list below it is no longer showing.</para>
    /// </summary>
    /// <param name="volume">The volume <paramref name="tree"/> is the whole of. See <see cref="Volume"/>.</param>
    private void Show(ExploreTree tree, ExplorePosition position, VolumeSpace volume)
    {
        var redraw = ExploreRedraw.Between(Tree, _position, tree, position);

        Tree = tree;
        Volume = volume;
        _position = position;

        OnPropertyChanged(nameof(CurrentNode));
        AscendCommand.NotifyCanExecuteChanged();

        if (redraw.KeepsSelection)
        {
            Selection.Carry(tree);
        }
        else
        {
            Selection.Show(tree);
        }

        ShowRows(tree, position.Node, redraw.KeepsRows);
        BuildTrail(tree);

        // What the pointer is over is the map's to say, and it says it again for the drawing this
        // redraw is about to produce. Clearing it here left a reader who had not moved with the
        // outline still round a shape and nothing under the map naming it.
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

        _arriving.Clear();

        foreach (var child in tree.ChildrenOf(node))
        {
            if (!Selection.WasRemoved(child))
            {
                _arriving.Add(child);
            }
        }

        LiveList.Show(
            Rows,
            _arriving,
            row => row.Key,
            child => ExploreRow.KeyOf(tree, child),
            child => new ExploreRow(tree, child, total, now, _guide),
            (row, _) => row.Describe(tree, total, now));
    }

    /// <summary>
    /// The path back to the top, brought up to date rather than rebuilt: a crumb is a button a
    /// reader can put the keyboard on, and clearing the collection destroys it. A walked scan
    /// publishes a snapshot every few hundred milliseconds, and every one of those redraws this.
    ///
    /// <para>The steps are <see cref="ExplorePosition.Trail"/>'s. The volume's step is named for
    /// the whole drive rather than by the root's name, because the root is the step after it.</para>
    /// </summary>
    private void BuildTrail(ExploreTree tree)
    {
        _position.Trail(tree, Volume, _steps);
        _trail.Clear();

        foreach (var step in _steps)
        {
            _trail.Add(new ExploreCrumb(
                step,
                step.IsVolume(tree, Volume) ? "Whole drive" : tree.NameOf(step.Node),
                FollowsAnother: _trail.Count > 0));
        }

        LiveList.Show(Trail, _trail, crumb => crumb.Position);
    }

    /// <summary>
    /// Read the volumes again where the last reading has gone stale, so the picker offers what is
    /// mounted now and states how full it is now. See <see cref="DriveList"/> for how often that is.
    ///
    /// <para>Called as the page is shown and as the picker opens rather than once at startup. A
    /// mount point does not go stale, but the space figures beside it do: a build, a download or a
    /// removal through this very page moves them.</para>
    ///
    /// <para>The chosen volume keeps its row, because a row is written over rather than replaced.
    /// Only a volume that has gone takes the selection with it.</para>
    /// </summary>
    public void RefreshDrives()
    {
        // Read before the volumes are, because the picker can write null over the selection while
        // the list under it changes. See _retargeting.
        var standing = Target;
        bool read;

        _retargeting = true;

        try
        {
            read = _drives.Refresh();
        }
        finally
        {
            _retargeting = false;
        }

        if (!read)
        {
            return;
        }

        // Where the reading leaves the page, by DriveList.Choose and ExploreTarget.AfterReading.
        // Retarget ends by explaining a refusal, which is what keeps the page from opening pointed
        // at a refused volume with the button dead and nothing said.
        Retarget(standing, standing.AfterReading(_drives.Choose(standing.Drive)?.RootPath));

        // A reading can change whether the chosen volume is refused without changing which row is
        // chosen — the row is written over, not replaced — so the selection raises nothing and the
        // two commands would go on answering for the last reading. §7.1: a button that scans a
        // volume the status line has just refused is the page stating one thing and doing another.
        ScanCommand.NotifyCanExecuteChanged();
        ElevateAndRescanCommand.NotifyCanExecuteChanged();
    }

    private bool CanScan() => !IsBusy && Target.IsScannable(_volumes);

    /// <summary>
    /// State why the page will not scan what it is pointed at, and take the sentence back once it
    /// is pointed somewhere it will.
    ///
    /// <para>§7.1 asks a refusal to say what it is, and a greyed-out Scan button says nothing at
    /// all. The retraction is the other half of that: this line describes what pressing Scan would
    /// do, so leaving "Deguffer does not scan this drive" up while the button is live and aimed at
    /// an ordinary volume states the opposite of the truth. Choosing a folder on a local disk after
    /// looking at a cloud one reaches it in two gestures.</para>
    ///
    /// <para>It takes back its own sentence and nobody else's. A scan's result and a selection's
    /// report write this same line, and neither is made untrue by a change of target, so clearing
    /// unconditionally would throw away whichever of them was there.</para>
    /// </summary>
    private void ExplainRefusal()
    {
        if (Target.Refusal(_volumes) is { } refused)
        {
            Status = refused;
            _statedRefusal = refused;
            return;
        }

        if (_statedRefusal is not null && Status == _statedRefusal)
        {
            Status = ScanPrompt;
        }

        _statedRefusal = null;
    }

    private bool CanRun() => !IsBusy;

    private bool CanAscend() => Tree is { } tree && _position.Up(tree, Volume) is not null;
}
