using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;
using Deguffer.Core.Working;

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
/// where each way of re-pointing the page leaves it (<see cref="ExploreTarget"/>), what it shows
/// round its picture (<see cref="ExploreDisplay"/>), when it may act (<see cref="ExploreGate"/>),
/// which notes it shows (<see cref="ExploreNotes"/>) and what its status line says
/// (<see cref="ExploreStatusText"/>). What is left is
/// one page's controller, and its parts are not independently useful — the scan, the scope, the
/// navigation and the view selection all read and write the same current tree and current node, so
/// a second type over them would share that state rather than own any of it.</para>
/// </summary>
public sealed partial class ExploreViewModel : ObservableObject, IMapNavigator
{
    private readonly IExploreScanner _scanner;
    private readonly IVolumeInventory _volumes;
    private readonly TimeProvider _time;

    /// <summary>The kept summaries a finished scan of a whole volume is compared with and added to.</summary>
    private readonly ScanHistory _history;

    /// <summary>What Windows states about the space on a scanned volume that no folder holds.</summary>
    private readonly IHiddenSpaceSource _hidden;

    /// <summary>Whether this process holds administrator rights, which decides the elevation offer.</summary>
    private readonly bool _isElevated;

    /// <summary>
    /// Which route measured the tree on screen, which decides what the unaccounted block can be made
    /// of: a walk misses cluster rounding, and the file table does not.
    /// </summary>
    private ScanStrategy _strategy = ScanStrategy.ParallelEnumeration;

    /// <summary>
    /// Starts an elevated replacement pointed where this page is, and says whether one started. A
    /// real relaunch raises the UAC prompt, which is why it is handed in.
    /// </summary>
    private readonly Func<ExploreRequest, bool> _relaunch;

    /// <summary>What is changing the machine on every page, which the elevated relaunch waits for.</summary>
    private readonly RunningActions _running;
    private readonly BusyWork _work;

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

    /// <summary>Where the reader has been, for Back and Forward. See <see cref="ExploreVisits"/>.</summary>
    private readonly ExploreVisits _visits = new();

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
    /// through its two-way binding while <see cref="RefreshDrivesAsync"/> is writing the rows, before
    /// this has put the selection back. Taken at face value that is the user choosing a different
    /// drive, so it drops the folder scope — and the refresh can happen as the picker opens, when
    /// somebody is only looking at the list. Rows are written over in place, so only a reading that
    /// finds the chosen drive gone still does this.</para>
    /// </summary>
    private bool _retargeting;

    /// <param name="time">
    /// The one clock the page reads: what the folder list and the line under the map date an age
    /// against, what a kept scan is stamped with, and what decides when the drive picker's last
    /// reading has gone stale.
    /// </param>
    /// <param name="isElevated">Whether this process holds administrator rights.</param>
    /// <param name="relaunch">See <see cref="_relaunch"/>.</param>
    /// <param name="running">See <see cref="_running"/>.</param>
    /// <param name="history">See <see cref="_history"/>.</param>
    /// <param name="motion">How the reader wants things to move, which a play of the replay follows.</param>
    internal ExploreViewModel(
        IExploreScanner scanner,
        IVolumeInventory volumes,
        IHiddenSpaceSource hidden,
        TimeProvider time,
        ExploreActions actions,
        ItemGuide guide,
        bool isElevated,
        Func<ExploreRequest, bool> relaunch,
        RunningActions running,
        BusyWork work,
        ScanHistory history,
        IMotionPolicy motion)
    {
        _scanner = scanner;
        _volumes = volumes;
        _hidden = hidden;
        _time = time;
        _history = history;
        _drives = new DriveList(volumes, time);
        _guide = guide;
        _isElevated = isElevated;
        _relaunch = relaunch;
        _running = running;
        _work = work;

        Selection = new ExploreSelection(actions, work);
        Growth = new ExploreGrowth(history);
        Replay = new ExploreReplay(motion);
        Files = new ExploreFiles(actions, time);
        Types = new ExploreTypes();

        // A comparison that arrives, or stops, recolours a map coloured by growth, and the kinds of a
        // finished scan arriving recolour a map coloured by type.
        Growth.Changed += (_, _) =>
        {
            FollowReplay();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        };
        Replay.Changed += (_, _) =>
        {
            StandDown();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        };
        Types.Changed += (_, _) => ViewChanged?.Invoke(this, EventArgs.Empty);

        // A clean or a removal on another page ends with this process as surely as one here.
        _running.Changed += (_, _) => ElevateAndRescanCommand.NotifyCanExecuteChanged();

        // A removal and a scan of the same drive have no business overlapping, so each stands the
        // other down through the one busy flag. One direction each, rather than a flag both write:
        // the selection says when it is working, and this says when the page is free to act.
        Selection.Working += (_, working) => IsBusy = working;
        Selection.Reported += (_, report) => Status.Say(report);
        Selection.Removed += (_, removed) => ShowLeft(removed);

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

        // The drives are not read here: a constructor cannot wait for a reading taken off the UI
        // thread, and the page reads them as it is first shown (RefreshDrivesAsync).

        // Offered before anything has been scanned, so an elevated scan does not have to be reached
        // through the walked one it replaces.
        OfferElevation(null);
    }

    /// <summary>What the user picked out by hand, and what §7.1 lets them do with it.</summary>
    public ExploreSelection Selection { get; }

    /// <summary>What grew since the last scan of the volume on screen. See <see cref="ExploreGrowth"/>.</summary>
    public ExploreGrowth Growth { get; }

    /// <summary>The map moving from the earlier scan to now. See <see cref="ExploreReplay"/>.</summary>
    public ExploreReplay Replay { get; }

    /// <summary>The largest files below the folder on screen, for the Files layout. See <see cref="ExploreFiles"/>.</summary>
    public ExploreFiles Files { get; }

    /// <summary>What kind of file fills the folder on screen, for a map coloured by type. See <see cref="ExploreTypes"/>.</summary>
    public ExploreTypes Types { get; }

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

    partial void OnIsBusyChanged(bool value) => StandDown();

    /// <summary>When the page may act, from the work under way and the picture on the map. See <see cref="ExploreGate"/>.</summary>
    private ExploreGate Gate => new(IsBusy, Replay.Shows);

    /// <summary>Let the selection act only where <see cref="ExploreGate.SelectionMayAct"/> says it may (§7.1).</summary>
    private void StandDown() => Selection.CanAct = Gate.SelectionMayAct;

    /// <summary>
    /// Replay the comparison, the tree and the folder on screen, while the panel of what grew is
    /// open, and nothing otherwise. See <see cref="ExploreReplay.Follow"/>.
    /// </summary>
    private void FollowReplay()
    {
        Replay.Follow(Display.ShowsGrowth ? Growth.Comparison : null, Tree, CurrentNode);
        StandDown();
    }

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
    /// The line under the map: what the page last said, and the §5.6 checks a removal could not pass
    /// beside it. A refusal the page states is taken back by <see cref="ExplainRefusal"/> once the page
    /// is pointed somewhere it will scan, and nothing else on the line is.
    /// </summary>
    public StatusLineViewModel Status { get; } = new(StatusLine.Said(ExploreStatusText.Prompt));

    /// <summary>The sentence §5.5 requires beside a walked scan, or null when the table answered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRouteNote))]
    [NotifyPropertyChangedFor(nameof(ShowsNotes))]
    [NotifyPropertyChangedFor(nameof(ShowsNotesButton))]
    public partial string? RouteNote { get; set; }

    public bool HasRouteNote => !string.IsNullOrEmpty(RouteNote);

    /// <summary>Whether the reader has collapsed the notes into their button. See <see cref="ExploreNotes.Dismissed"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNotes))]
    [NotifyPropertyChangedFor(nameof(ShowsNotesButton))]
    public partial bool NotesDismissed { get; set; }

    /// <summary>The four notes and the reader's choice to collapse them. See <see cref="ExploreNotes"/>.</summary>
    private ExploreNotes Notes =>
        new(Selection.HasNote, Selection.HasStaleNote, HasViewNote, HasRouteNote, NotesDismissed);

    /// <summary>
    /// What the collapsed button shows, and what it is called: both change where one of the notes is
    /// a warning (<see cref="ExploreNotes.Warns"/>), so neither is colour alone.
    /// </summary>
    public string NotesButtonGlyph => Notes.Warns ? WarningGlyph : InfoGlyph;

    /// <inheritdoc cref="NotesButtonGlyph"/>
    public string NotesButtonLabel => Notes.Warns
        ? "Show the notes about this scan. One of them is a warning."
        : "Show the notes about this scan";

    /// <summary>Whether the notes are on the card. See <see cref="ExploreNotes.ShowsPanel"/>.</summary>
    public bool ShowsNotes => Notes.ShowsPanel;

    /// <summary>Whether the button that brings them back is on the card. See <see cref="ExploreNotes.ShowsButton"/>.</summary>
    public bool ShowsNotesButton => Notes.ShowsButton;

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
    [NotifyPropertyChangedFor(nameof(ShowsLegend))]
    [NotifyPropertyChangedFor(nameof(ShowsGrowth))]
    [NotifyPropertyChangedFor(nameof(ShowsTypes))]
    [NotifyPropertyChangedFor(nameof(ShowsFileFilters))]
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
    [NotifyPropertyChangedFor(nameof(SelectedScheme))]
    [NotifyPropertyChangedFor(nameof(ViewNote))]
    [NotifyPropertyChangedFor(nameof(HasViewNote))]
    [NotifyPropertyChangedFor(nameof(ShowsMapControls))]
    [NotifyPropertyChangedFor(nameof(ShowsLegend))]
    [NotifyPropertyChangedFor(nameof(ShowsFileFilters))]
    [NotifyPropertyChangedFor(nameof(ShowsNotes))]
    [NotifyPropertyChangedFor(nameof(ShowsNotesButton))]
    public partial ExploreView SelectedView { get; set; }

    partial void OnSelectedViewChanged(ExploreView value)
    {
        Files.IsActive = Display.SearchesFiles;

        // Each picture keeps its own colours, so a change of view can be a change of scheme.
        ShowLegend();

        // Whether a zoom is part of where the reader is turns on the view. See Zooms.
        NotifyVisits();
    }

    /// <summary>
    /// What the colours on the map are to say. See <see cref="ExploreColouring"/>.
    ///
    /// <para>Unlike <see cref="SelectedView"/> this is never substituted. A partial tree is coloured
    /// exactly as a finished one is — an age is a fact about a node rather than about the ordering
    /// of its siblings — so a scan in progress needs no sentence explaining this one away. Growth is
    /// compared only once a scan has finished, and until then the map paints every shape as not
    /// compared, which its legend names. A folder's kind of file is measured only once a scan has
    /// finished too, on the same terms; see <see cref="ExploreTypes.Dominant"/>.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLegend))]
    [NotifyPropertyChangedFor(nameof(ShowsGrowth))]
    [NotifyPropertyChangedFor(nameof(ShowsTypes))]
    public partial ExploreColouring SelectedColouring { get; set; }

    partial void OnSelectedColouringChanged(ExploreColouring value)
    {
        ShowLegend();
        Types.IsActive = Display.MeasuresTypes;
        FollowReplay();
    }

    /// <summary>
    /// How the maps are drawn, which holds the colours each picture is drawn in. The page hands it
    /// over as the appearance window changes it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedScheme))]
    [NotifyPropertyChangedFor(nameof(ShowsLegend))]
    public partial MapLook Look { get; set; } = MapLook.Default;

    partial void OnLookChanged(MapLook value) => ShowLegend();

    /// <summary>
    /// Which set of colours the picture on screen is drawn in. See <see cref="ExploreDisplay.Scheme"/>.
    /// Here rather than only on the page because the legend below has to be drawn in it too: a
    /// legend in one set of colours beside a map in another names every band wrongly.
    /// </summary>
    public ExploreScheme SelectedScheme => Display.Scheme;

    /// <summary>
    /// What the page shows round its picture, from what it is showing. See <see cref="ExploreDisplay"/>.
    /// </summary>
    private ExploreDisplay Display => new(Tree, SelectedView, SelectedColouring, Look, MapInContrast);

    /// <summary>What each colour on the map means. See <see cref="MapLegend"/>.</summary>
    public ObservableCollection<ExploreLegendBand> Legend { get; } = [];

    /// <summary>Bring <see cref="Legend"/> to the colouring and the scheme on screen, in place.</summary>
    private void ShowLegend() => LiveList.Rewrite(Legend, ExploreLegendBand.For(SelectedColouring, SelectedScheme));

    /// <summary>Whether to show that legend. See <see cref="ExploreDisplay.ShowsLegend"/>.</summary>
    public bool ShowsLegend => Display.ShowsLegend;

    /// <summary>
    /// Whether the map is drawn in a high contrast theme's two colours, in which its colours say nothing
    /// and no legend names them. The page says, as the map tells it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLegend))]
    public partial bool MapInContrast { get; set; }

    /// <summary>Whether to show the Files layout's filters. See <see cref="ExploreDisplay.ShowsFileFilters"/>.</summary>
    public bool ShowsFileFilters => Display.ShowsFileFilters;

    /// <summary>Whether to show the panel of what grew. See <see cref="ExploreDisplay.ShowsGrowth"/>.</summary>
    public bool ShowsGrowth => Display.ShowsGrowth;

    /// <summary>Whether to show the panel of what kind of file fills the folder on screen. See <see cref="ExploreDisplay.ShowsTypes"/>.</summary>
    public bool ShowsTypes => Display.ShowsTypes;

    /// <summary>How what is on screen differs from what the View box names, or null. See <see cref="ExploreDisplay.Note"/>.</summary>
    public string? ViewNote => Display.Note;

    public bool HasViewNote => ViewNote is not null;

    /// <summary>
    /// Whether the line naming how the mouse zooms and drags is worth showing: while the picture on
    /// screen is one that zooms. See <see cref="ExploreDisplay.Zooms"/>.
    /// </summary>
    public bool ShowsMapControls => Display.Zooms;

    /// <summary>
    /// How large the volume was and how much of it was free when the scan on screen finished, where
    /// it covered the whole of one, and <see cref="VolumeSpace.None"/> otherwise.
    ///
    /// <para>Read as the scan finishes, rather than kept current. Everything else on screen
    /// describes the disk as that scan found it, so a figure that moved on its own would set the
    /// blocks against sizes they no longer match. A removal from this page reads it again with the
    /// sizes it changed (<see cref="ShowLeft"/>), and the stale note says the rest of the drive is
    /// not scanned again.</para>
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

    /// <summary>
    /// The part of the map's picture on screen now, which a step away from here remembers so Back
    /// comes back to it. Asked at the moment of the step rather than followed, because the zoom moves
    /// at every frame of a glide. The page points this at its map, and it is the whole picture until
    /// it does.
    /// </summary>
    public Func<MapViewport> Viewing { get; set; } = () => MapViewport.Whole;

    /// <summary>
    /// The zoom Back or Forward is returning to, for the one redraw that step asks for, and null for
    /// every other. Handed over for that moment only, because every other redraw keeps the zoom the
    /// reader has now: a recolouring, a snapshot, a removal.
    /// </summary>
    public MapViewport? Revisiting { get; private set; }

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
    /// Whether the rows are being rewritten from here: this folder's, or the Files layout's.
    ///
    /// <para>Read by the page, because a bound <c>ListView</c> drops an item from its own
    /// selection when the collection under it stops holding that item where it was, and reports
    /// that back as a selection change. Taken for a gesture, it overwrites the selection with
    /// whatever the rewrite happened to leave behind — on a rescan that is a node number belonging
    /// to the tree before it, and asking the arriving tree for its path throws.</para>
    /// </summary>
    public bool IsShowingRows => _showingRows || Files.IsShowingRows;

    /// <summary>Whether <see cref="ShowRows"/> is rewriting <see cref="Rows"/>. Half of <see cref="IsShowingRows"/>.</summary>
    private bool _showingRows;

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
        var scoped = IsScopedToFolder;

        IsBusy = true;
        Progress = null;
        RouteNote = null;

        // What is on screen is about to be replaced, and a comparison describes one tree.
        Growth.Clear();

        // Started with the scan and never awaited here. Part of §7.1's refusal set says what is
        // running right now, so it goes stale while the page is open, and a scan is the moment the
        // rest of the page is being re-measured anyway. It finishes once the scan does, reading what
        // the scan listed rather than listing it again beside it, so it is given the scan, or null
        // where the scan ends without one.
        var scanned = new TaskCompletionSource<ExploreScan?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Selection.Reconsider(ProtectionScope.Scanned(target, scanned.Task, _volumes));

        // Back to what is known before anything is measured. The previous scan's fallback reason is
        // about to be replaced, and a cancelled or failed scan never reaches the offer below.
        OfferElevation(null);

        Status.Say(StatusLine.Said($"Scanning {target}…"));

        // Next to the try, so nothing can throw between the two and leave the taskbar busy.
        var work = _work.Begin();

        try
        {
            var scan = await _scanner.ScanAsync(
                target,
                new Progress<ExploreProgress>(progress =>
                {
                    Report(progress);
                    work.Report(progress.Fraction);
                }),
                ct);
            scanned.TrySetResult(scan);

            // Read afresh and handed to Show, which is what draws the map, because the space figures
            // the drive picker holds are from whenever it last opened. Read off the UI thread, since
            // the reading asks every volume the machine has mounted.
            await _volumes.RefreshAsync(ct);

            var volume = await VolumeSpace.ReadAsync(_volumes, _hidden, target, scan.Tree, ct);

            // Before the tree is shown, because the pointer can describe the new map from then on.
            _strategy = scan.Strategy;

            Show(scan.Tree, _position.CarriedTo(Tree, scan.Tree), volume);

            RouteNote = scan.RouteNote;
            OfferElevation(scan.Fallback);

            await RecordAsync(scan, target, scoped);

            Status.Say(StatusLine.Said(ExploreStatusText.Finished(scan, volume, scoped)));
        }
        catch (OperationCanceledException)
        {
            // The last snapshot goes with it. A partial tree covers only the folders read so far,
            // its HasUnknownSizes is false because nothing refused anything, and it draws and
            // navigates exactly like a finished scan — so leaving it on screen states a total for
            // the drive that is wrong by however much was left.
            Tree = null;
            Volume = VolumeSpace.None;
            NotifyVisits();
            Growth.Clear();
            Selection.Show(null);
            Files.Show(null, 0);
            Types.Show(null, 0);
            Rows.Clear();
            Trail.Clear();
            ViewChanged?.Invoke(this, EventArgs.Empty);

            Status.Say(StatusLine.Said("Scan cancelled. Nothing was measured."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A volume that went away mid-scan, or one the account cannot open at all. Neither is
            // worth taking the window down for, and the page is read-only — there is nothing
            // half-done to report.
            Status.Say(StatusLine.Said($"Could not scan {target}: {ex.Message}"));
            work.Fail();
        }
        finally
        {
            scanned.TrySetResult(null);
            IsBusy = false;
            Progress = null;
            work.Dispose();
        }
    }

    /// <summary>
    /// Compare a finished scan with the last kept scan of its volume, and keep it for the next one.
    /// Only a scan of a whole volume, because a summary of one folder says nothing about what else
    /// on the drive grew.
    ///
    /// <para>Not cancellable. The scan has finished by now, the work is a pass over folders already
    /// in memory and one small file, and a cancel arriving during it would otherwise throw away the
    /// finished scan along with it.</para>
    /// </summary>
    /// <param name="scoped">Whether the scan was started on a folder rather than a drive.</param>
    private async Task RecordAsync(ExploreScan scan, string target, bool scoped)
    {
        var volume = HostVolume.For(_volumes, target)?.VolumeName;
        var space = Volume;
        var taken = _time.GetUtcNow().UtcDateTime;

        var record = await Task.Run(() => _history.Record(scan, volume, space, scoped, taken));

        // A comparison describes one scan, and the page can have moved to another while this ran.
        // Shown only for the scan it was taken from, or what a removal left of it.
        if (ReferenceEquals(Tree?.Scanned, scan.Tree))
        {
            Growth.Show(record);
        }
    }

    /// <summary>
    /// Show what is inside the folder <paramref name="row"/> names, from the list of what grew, where
    /// <see cref="ExploreGrowth.Opens"/> says it can be opened.
    /// </summary>
    public void ShowFolder(ExploreGrowthRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (Tree is { } tree && Growth.Opens(tree, row))
        {
            Visit(tree, ExplorePosition.Inside(row.Node));
        }
    }

    /// <summary>
    /// Point the next scan at <paramref name="folder"/> and everything below it.
    ///
    /// <para>The picking itself belongs to the page: it is a WinUI dialog needing a window handle,
    /// and this stays testable by knowing only about the path that comes back — the arrangement
    /// <see cref="SettingsViewModel.AddSourceRoot"/> already uses for the same dialog.</para>
    ///
    /// <para>A choice that is not a folder on a disk leaves the target where it was and says so,
    /// by <see cref="PickedFolder.OnDisk"/>.</para>
    /// </summary>
    /// <param name="folder">What the picker returned, which may name nothing on a disk.</param>
    public void ScopeTo(string folder)
    {
        if (PickedFolder.OnDisk(folder) is not { } onDisk)
        {
            Status.Say(StatusLine.Refused(ExploreStatusText.NotOnDisk(Target.Refusal(_volumes))));

            return;
        }

        Retarget(Target, Target.ScopedTo(onDisk, _drives.Holding(onDisk)?.RootPath));
    }

    /// <summary>
    /// The page being shown: read the drives (<see cref="RefreshDrivesAsync"/>), then point it at
    /// <paramref name="resuming"/> where an elevated relaunch asked for a target.
    ///
    /// <para>In that order and in one place, because <see cref="PointAt"/> finds the drive among the
    /// rows, and the rows are filled only once the reading lands off the UI thread. Pointed before
    /// then, a relaunch to rescan a whole drive found no row, and scanned nothing.</para>
    /// </summary>
    /// <returns>Whether the page now points where <paramref name="resuming"/> asked, so the caller may scan.</returns>
    public async Task<bool> ShownAsync(ExploreRequest? resuming, CancellationToken ct)
    {
        await RefreshDrivesAsync(ct);

        return resuming is { } requested && PointAt(requested.Drive, requested.Folder);
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
        // Asked of the rows, so it means something only once the drives have been read: see
        // ShownAsync, which is how the page calls it.
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
    /// contradict — and it is why this asks that command's <c>CanExecute</c> rather than only
    /// whether the page is busy. A relaunch with nothing to point at leaves the replacement
    /// waiting on a page the user did not ask to be on.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanElevateNow))]
    private void ElevateAndRescan()
    {
        if (!_relaunch(new ExploreRequest(Target.Drive, Target.Folder)))
        {
            Status.Say(StatusLine.Said(
                "Deguffer is still running without administrator rights, so it scans by walking "
                + "directories. Everything else works exactly the same."));
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
            ? ElevationOffer.ShouldOffer(_isElevated, fallback, Volume.Hidden)
            : ElevationOffer.ShouldOffer(_isElevated);

        OnPropertyChanged(nameof(ElevateLabel));
    }

    /// <summary>
    /// What a screen reader is told of <paramref name="node"/> of <paramref name="tree"/> on the map: its
    /// name, whether it is a folder, and whether opening it shows what it holds (<see cref="Descend"/>).
    /// Nothing of a tree other than the one this page scanned, such as a step of a replay of what grew,
    /// whose node numbers mean nothing here.
    /// </summary>
    public MapShapeFacts? ShapeFacts(ISizedTree tree, int node) =>
        Scanned(tree, node) is { } scanned
            ? new MapShapeFacts(
                scanned.NameOf(node),
                scanned.IsDirectory(node) ? "Folder" : "File",
                _position.Opening(scanned, node, Volume) is not null)
            : null;

    /// <summary>
    /// Whether <paramref name="node"/> of <paramref name="tree"/> may be removed, and if not why (§7.1),
    /// for a screen reader to hear before Delete is pressed. Nothing of a tree other than the one this
    /// page scanned, as for <see cref="ShapeFacts"/>.
    ///
    /// <para>Apart from the facts because it asks the machine where the item's volume is mounted, each
    /// time it is asked, and a search of the window names every shape it meets.</para>
    /// </summary>
    public ExploreVerdict? ShapeRemoval(ISizedTree tree, int node) =>
        Scanned(tree, node) is { } scanned ? Selection.VerdictOf(scanned.PathOf(node)) : null;

    private ExploreTree? Scanned(ISizedTree tree, int node) =>
        ReferenceEquals(tree, Tree) && Tree is { } scanned && scanned.Holds(node) ? scanned : null;

    /// <summary>
    /// Show what is inside <paramref name="node"/>. Ignored for anything the tree on screen no longer
    /// holds, removed since the scan, and wherever <see cref="ExplorePosition.Opening"/> says the step
    /// leads nowhere.
    /// </summary>
    public void Descend(int node)
    {
        if (Tree is not { } tree || !tree.Holds(node)
            || _position.Opening(tree, node, Volume) is not { } opened)
        {
            return;
        }

        Visit(tree, opened);
    }

    /// <summary>Up a folder from the map, as the command goes up from a key or the button.</summary>
    void IMapNavigator.Ascend() => AscendCommand.TryExecute();

    /// <summary>A shape picked on the map is the whole selection, and a place with no shape is none.</summary>
    void IMapNavigator.Pick(int? node) => Selection.Select(node is { } picked ? [picked] : []);

    [RelayCommand(CanExecute = nameof(CanAscend))]
    private void Ascend()
    {
        if (Tree is { } tree && _position.Up(tree, Volume) is { } up)
        {
            Visit(tree, up);
        }
    }

    /// <summary>Go straight to a step on the trail.</summary>
    public void GoTo(ExplorePosition position)
    {
        if (Tree is { } tree)
        {
            Visit(tree, position);
        }
    }

    /// <summary>
    /// The map zoomed to a shape it could not open, from <paramref name="from"/>. A step like
    /// opening a folder, so Back zooms out again; a turn of the wheel or a drag is not, as scrolling
    /// a page is not a step in a browser.
    /// </summary>
    public void Zoomed(MapViewport from)
    {
        if (Tree is null)
        {
            return;
        }

        _visits.Leave(new ExploreVisit(_position, from));
        NotifyVisits();
    }

    /// <summary>Back to where the reader was before their last step, as a browser's Back button goes.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (Tree is { } tree)
        {
            Revisit(tree, _visits.Back(Here, tree, Volume, Zooms));
        }
    }

    /// <summary>Forward again to where Back left, as a browser's Forward button goes.</summary>
    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (Tree is { } tree)
        {
            Revisit(tree, _visits.Forward(Here, tree, Volume, Zooms));
        }
    }

    /// <summary>Where the reader is now, zoom and all, as a step on <see cref="_visits"/>.</summary>
    private ExploreVisit Here => new(_position, Viewing());

    /// <summary>Whether a zoom is part of where the reader is. See <see cref="ExploreDisplay.Zooms"/>.</summary>
    private bool Zooms => Display.Zooms;

    /// <summary>
    /// Show <paramref name="to"/>: a step somewhere new, where the reader was going behind them. The
    /// place already on screen is not somewhere new, so it is shown again without being a step, and
    /// what lay ahead stays ahead.
    /// </summary>
    private void Visit(ExploreTree tree, ExplorePosition to)
    {
        if (!to.Shows(_position, tree, Volume))
        {
            _visits.Leave(Here);
        }

        Show(tree, to, Volume);
    }

    /// <summary>
    /// Show <paramref name="visit"/> again, at the zoom it was left at. Within the same place only the
    /// zoom moves, so the rows, the selection and the trail stay as they are.
    /// </summary>
    private void Revisit(ExploreTree tree, ExploreVisit? visit)
    {
        if (visit is not { } to)
        {
            return;
        }

        Revisiting = to.Viewport;

        try
        {
            if (to.Position == _position)
            {
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Show(tree, to.Position, Volume);
            }
        }
        finally
        {
            Revisiting = null;
        }

        // Asked again once the redraw has put the zoom where it was going: until then the map still
        // answers with the zoom being left, and a step back to the same place at another zoom would
        // read as a step to where the reader already is.
        NotifyVisits();
    }

    /// <summary>
    /// The map's zoom is somewhere new. Where Back and Forward lead turns on it, because a step to the
    /// place on screen at the zoom on screen goes nowhere.
    /// </summary>
    public void ViewportChanged() => NotifyVisits();

    /// <summary>Say that where Back and Forward lead may have changed.</summary>
    private void NotifyVisits()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
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

            ({ } tree, { IsUnaccounted: true } unaccounted) => (
                "In use, but not accounted for by this scan",
                FreeSpace.Format(unaccounted.Bytes),
                ExploreUnaccountedNote.For(_isElevated, Volume, tree.TotalBytes, _strategy)),

            (_, { IsShadowCopies: true } shadowCopies) => (
                "Restore points and shadow copies, by Windows' own figure",
                FreeSpace.Format(shadowCopies.Bytes),
                HiddenSpaceNote.ShadowCopies(Volume.Hidden.ShadowCopies)),

            (_, { IsReservedStorage: true } reserved) => (
                "Reserved storage, by Windows' own figure",
                FreeSpace.Format(reserved.Bytes),
                HiddenSpaceNote.ReservedStorage()),

            ({ } tree, { IsNode: true } node) => Over(tree, node.Node),

            _ => (string.Empty, string.Empty, string.Empty),
        };

        // The card beside the map marks the row of what is pointed at, so the map answers "what" too.
        var pointed = hit is { IsNode: true } over ? over.Node : (int?)null;

        Types.Point(Tree, pointed);
        Growth.Point(Tree, pointed);
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
        var storage = ExploreRowText.Storage(tree, node);
        var figures = $"{FreeSpace.Format(tree.SizeOf(node))} on disk, "
            + (storage.Length > 0 ? $"{storage}, " : string.Empty)
            + $"last written {ExploreRowText.Age(tree, node, _time.GetUtcNow().UtcDateTime)}";

        // The shape's own change, where the map is coloured by growth: compared, or settled by its
        // dates. A shape painted by the folder above it has none of its own, and the readout says
        // nothing rather than borrow that one.
        if (SelectedColouring == ExploreColouring.Growth
            && Growth.Comparison is { } growth && growth.Describes(tree)
            && growth.ChangeOf(node) is { } change)
        {
            figures += $", {GrowthText.Change(change, growth.Earlier.UnrecordedAtMost)} since the last scan";
        }

        // The kind the shape is painted, where the map is coloured by type, so the colour is never the
        // only thing saying it (§6.5). A folder not measured yet says nothing rather than guess.
        if (SelectedColouring == ExploreColouring.Type && DominantTypes.KindOf(tree, node, Types.Dominant) is { } category)
        {
            var kind = FileCategories.Label(category).ToLowerInvariant();

            figures += tree.IsDirectory(node) ? $", largest kind: {kind}" : $", {kind}";
        }

        // The level the shape is painted for, where the map is coloured by depth, for the same reason.
        if (SelectedColouring == ExploreColouring.Depth && DepthPalette.LevelOf(tree, node, CurrentNode) is { } level)
        {
            figures += $", {DepthPalette.Name(level).ToLowerInvariant()}";
        }

        return (path, figures, Joined(_guide.DescribeNearest(path)?.Tip(), HiddenSpaceNote.For(tree, node, Volume)));
    }

    /// <summary>
    /// What the reference says about a shape, then what Windows states about it, either of which may
    /// be absent.
    /// </summary>
    private static string Joined(string? described, string stated) =>
        string.Join("\n\n", new[] { described, stated }.Where(part => !string.IsNullOrEmpty(part)));

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

        Status.Say(StatusLine.Said(progress.Total is null
            ? $"Scanning… {progress.Done:N0} items, {FreeSpace.Format(progress.BytesSeen)} so far"
            : $"Reading the file table… {progress.Fraction:P0}"));

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

        // Before the tree is replaced, because the steps' node numbers belong to the one leaving.
        _visits.Carry(Tree, tree);

        Tree = tree;
        Volume = volume;
        _position = position;

        OnPropertyChanged(nameof(CurrentNode));
        AscendCommand.NotifyCanExecuteChanged();
        NotifyVisits();

        if (redraw.KeepsSelection)
        {
            Selection.Carry(tree);
        }
        else
        {
            Selection.Show(tree);
        }

        ShowRows(tree, position.Node, redraw.KeepsRows);
        Files.Show(tree, position.Node);
        Types.Show(tree, position.Node);
        BuildTrail(tree);
        FollowReplay();

        // What the pointer is over is the map's to say, and it says it again for the drawing this
        // redraw is about to produce. Clearing it here left a reader who had not moved with the
        // outline still round a shape and nothing under the map naming it.
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Show what is left of the tree on screen after a removal from it, without rescanning, as a
    /// snapshot of the same place is shown: the reader stays where they were, the rows are brought up
    /// to date in place, and the map moves to the new layout, where what went shrinks away and its
    /// neighbours grow into the room it left.
    ///
    /// <para>The volume is read again, because a removal frees what it deleted, and drawn from the old
    /// figures that would be space no folder accounts for (<see cref="VolumeSpace.Remeasured"/>).</para>
    /// </summary>
    private void ShowLeft(ExploreSelection.RemovedFrom removed)
    {
        if (!ReferenceEquals(Tree, removed.Tree))
        {
            return;
        }

        var left = removed.Left;

        Show(left, _position.CarriedTo(removed.Tree, left), Volume.Remeasured(_volumes, left.RootPath));
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
        _showingRows = true;

        try
        {
            Rewrite(tree, node, reconcile);
        }
        finally
        {
            _showingRows = false;
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
        var now = _time.GetUtcNow().UtcDateTime;

        _arriving.Clear();

        foreach (var child in tree.ChildrenOf(node))
        {
            _arriving.Add(child);
        }

        LiveList.Show(
            Rows,
            _arriving,
            row => row.Key,
            child => ExploreRow.KeyOf(tree, child),
            child => new ExploreRow(tree, child, total, now, _guide),
            (row, child) => row.Describe(tree, child, total, now));
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
    ///
    /// <para>The machine is read off the UI thread, and the rows are brought up to date on it once
    /// the reading lands. A choice the user makes while the reading runs is theirs, so only the
    /// writing of the rows is guarded.</para>
    /// </summary>
    public async Task RefreshDrivesAsync(CancellationToken ct)
    {
        if (await _drives.ReadAsync(ct) is not { } volumes)
        {
            return;
        }

        // Read before the rows change, because the picker can write null over the selection while
        // the list under it changes. See _retargeting.
        var standing = Target;

        _retargeting = true;

        try
        {
            _drives.Show(volumes);
        }
        finally
        {
            _retargeting = false;
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

    private bool CanScan() => Gate.MayScan(Target, _volumes);

    private bool CanElevateNow() => Gate.MayElevate(Target, _volumes, _running);

    /// <summary>
    /// State why the page will not scan what it is pointed at, and take the sentence back once it
    /// is pointed somewhere it will. See <see cref="ExploreStatusText.Retargeted"/>.
    /// </summary>
    private void ExplainRefusal()
    {
        Status.Say(ExploreStatusText.Retargeted(Status.Line, Target.Refusal(_volumes)));
    }

    private bool CanRun() => !IsBusy;

    private bool CanAscend() => Tree is { } tree && _position.Up(tree, Volume) is not null;

    private bool CanGoBack() => Tree is { } tree && _visits.CanGoBack(Here, tree, Volume, Zooms);

    private bool CanGoForward() => Tree is { } tree && _visits.CanGoForward(Here, tree, Volume, Zooms);
}
