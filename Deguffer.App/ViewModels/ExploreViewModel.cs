using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Diagnostics;
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
/// Drives the Explore page: scan what it is pointed at, and move around what was found.
///
/// <para>This orchestrates. It holds no knowledge of how a volume is read — that is
/// <see cref="ExploreScanner"/>'s, and it chooses between §5.5's two routes on its own — none of
/// how a rectangle is drawn, which belongs to the layout and the rasteriser in Core, and none of
/// how a node is worded, which is <see cref="ExploreNodeText"/>'s and <see cref="ExploreReadout"/>'s.
/// What is left here is the tree on screen and the node being looked at (G2).</para>
///
/// <para>Every part of the page that does not read those two is a type beside this one, which it
/// hands what it needs: what the page is pointed at (<see cref="ExploreTargeting"/>) and the offer to
/// scan it as administrator (<see cref="ExploreElevation"/>), the notes (<see cref="ExploreNoteCard"/>),
/// the status line (<see cref="StatusLineViewModel"/>), the selection, what grew, the replay, the
/// Files layout and the kinds of file. So are the parts that do read them but hold state of their
/// own: the list and its trail (<see cref="ExploreRows"/>) and Back and Forward
/// (<see cref="ExploreVisiting"/>). The rules are Core's: where a step leads
/// (<see cref="ExplorePosition"/>), what a redraw keeps (<see cref="ExploreRedraw"/>), what the page
/// shows round its picture (<see cref="ExploreDisplay"/>) and when it may act
/// (<see cref="ExploreGate"/>).</para>
///
/// <para><b>Past the 500 lines at which G1 asks for a reason, and the reason is what remains.</b>
/// The scan, the readout under the pointer, the view and colouring the reader chose and the steps
/// into and out of folders all read and write the one tree and the one node, and each is a few
/// members long. A type over any of them would share that state rather than own any of it.</para>
/// </summary>
public sealed partial class ExploreViewModel : ObservableObject, IMapNavigator, IMapShapes<ExploreTree>
{
    private readonly IExploreScanner _scanner;

    // SelectedView, as the scan's reporting thread reads it when it takes each snapshot. Volatile so
    // that thread sees a change of view made on this one at the next snapshot rather than never.
    private volatile ExploreView _viewForSnapshots;
    private readonly IVolumeInventory _volumes;
    private readonly TimeProvider _time;

    /// <summary>The kept summaries a finished scan of a whole volume is compared with and added to.</summary>
    private readonly ScanHistory _history;

    /// <summary>What Windows states about the space on a scanned volume that no folder holds.</summary>
    private readonly IHiddenSpaceSource _hidden;

    /// <summary>
    /// Whether this process holds administrator rights, which decides what the pointer says of the
    /// space a scan could not account for.
    /// </summary>
    private readonly bool _isElevated;

    /// <summary>
    /// Which route measured the tree on screen, which decides what the unaccounted block can be made
    /// of: a walk misses cluster rounding, and the file table does not.
    /// </summary>
    private ScanStrategy _strategy = ScanStrategy.ParallelEnumeration;

    private readonly BusyWork _work;

    /// <summary>Where the views are: see <see cref="ExplorePosition"/>. Written only by <see cref="Show"/>.</summary>
    private ExplorePosition _position;

    /// <summary>
    /// What the app knows about well-known files and folders, resolved against this machine once
    /// and read from here on (G5). Held rather than reached for statically, so a test that hands the
    /// page a synthetic profile gets a page that explains that profile's contents.
    /// </summary>
    private readonly ItemGuide _guide;

    /// <param name="time">
    /// The one clock the page reads: what the folder list and the line under the map date an age
    /// against, what a kept scan is stamped with, and what decides when the drive picker's last
    /// reading has gone stale.
    /// </param>
    /// <param name="isElevated">Whether this process holds administrator rights.</param>
    /// <param name="relaunch">See <see cref="ExploreElevation"/>.</param>
    /// <param name="running">What is changing the machine on every page, which the elevated relaunch waits for.</param>
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
        _guide = guide;
        _isElevated = isElevated;
        _work = work;

        Selection = new ExploreSelection(actions, work);
        Growth = new ExploreGrowth(history);
        Replay = new ExploreReplay(motion);
        Files = new ExploreFiles(actions, time);
        Types = new ExploreTypes();
        Targeting = new ExploreTargeting(volumes, time, Status);
        Elevation = new ExploreElevation(Targeting, volumes, isElevated, relaunch, running, Status);
        Notes = new ExploreNoteCard(Selection);
        Listing = new ExploreRows(guide, time);
        Visiting = new ExploreVisiting(
            () => Tree is { } tree ? new ExploreOnScreen(tree, _position, Volume, Display.Zooms) : null,
            (tree, to) => Show(tree, to, Volume, ShowCause.Step),
            () => ViewChanged?.Invoke(this, EventArgs.Empty));

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

        Targeting.Retargeted += (_, _) => ScanCommand.NotifyCanExecuteChanged();

        // A removal and a scan of the same drive have no business overlapping, so each stands the
        // other down through the one busy flag. One direction each, rather than a flag both write:
        // the selection says when it is working, and this says when the page is free to act.
        Selection.Working += (_, working) => IsBusy = working;
        Selection.Reported += (_, report) => Status.Say(report);
        Selection.Removed += (_, removed) => ShowLeft(removed);

        StandDown();
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

    /// <summary>What the page is pointed at. See <see cref="ExploreTargeting"/>.</summary>
    public ExploreTargeting Targeting { get; }

    /// <summary>The offer to scan that as administrator. See <see cref="ExploreElevation"/>.</summary>
    public ExploreElevation Elevation { get; }

    /// <summary>The notes in the corner of the card. See <see cref="ExploreNoteCard"/>.</summary>
    public ExploreNoteCard Notes { get; }

    /// <summary>What the folder on screen holds, and the trail above it. See <see cref="ExploreRows"/>.</summary>
    public ExploreRows Listing { get; }

    /// <summary>Back and Forward. See <see cref="ExploreVisiting"/>.</summary>
    public ExploreVisiting Visiting { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsBusy { get; private set; }

    partial void OnIsBusyChanged(bool value) => StandDown();

    /// <summary>When the page may act, from the work under way and the picture on the map. See <see cref="ExploreGate"/>.</summary>
    private ExploreGate Gate => new(IsBusy, Replay.Shows);

    /// <summary>
    /// Hand <see cref="Gate"/> to every part that acts, so the selection acts only where
    /// <see cref="ExploreGate.SelectionMayAct"/> says it may (§7.1).
    /// </summary>
    private void StandDown()
    {
        var gate = Gate;

        Selection.CanAct = gate.SelectionMayAct;
        Targeting.Gate = gate;
        Elevation.Gate = gate;
    }

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
    public partial double? Progress { get; private set; }

    /// <summary>
    /// The same figure as a plain number, because a progress bar's value is not nullable and a
    /// binding that has to fall back is a binding that fails silently when it is wrong.
    /// </summary>
    public double ProgressValue => Progress ?? 0;

    /// <summary>Whether the bar has to be indeterminate. See <see cref="Progress"/>.</summary>
    public bool HasNoProgressFraction => Progress is null;

    /// <summary>
    /// The line under the map: what the page last said, and the §5.6 checks a removal could not pass
    /// beside it. A refusal the page states is taken back by <see cref="ExploreTargeting"/> once the page
    /// is pointed somewhere it will scan, and nothing else on the line is.
    /// </summary>
    public StatusLineViewModel Status { get; } = new(StatusLine.Said(ExploreStatusText.Prompt));

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
    [NotifyCanExecuteChangedFor(nameof(AscendCommand))]
    public partial ExploreTree? Tree { get; private set; }

    partial void OnTreeChanged(ExploreTree? value) => Notes.HasViewNote = HasViewNote;

    /// <summary>
    /// Which picture the user asked for.
    ///
    /// <para>Here rather than only in the page, because the page is not the only thing that has to
    /// answer for it. A running scan hands its snapshots over in the order this view needs
    /// (<see cref="ExploreSurface.SnapshotOrder"/>). Changed while one is on screen, the map can draw
    /// the icicle until the next arrives, and <see cref="ViewNote"/> is what keeps that from being a
    /// silent substitution.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedScheme))]
    [NotifyPropertyChangedFor(nameof(ViewNote))]
    [NotifyPropertyChangedFor(nameof(HasViewNote))]
    [NotifyPropertyChangedFor(nameof(ShowsMapControls))]
    [NotifyPropertyChangedFor(nameof(ShowsLegend))]
    [NotifyPropertyChangedFor(nameof(ShowsFileFilters))]
    public partial ExploreView SelectedView { get; set; }

    partial void OnSelectedViewChanged(ExploreView value)
    {
        _viewForSnapshots = value;

        Files.IsActive = Display.SearchesFiles;
        Listing.IsActive = Display.ListsRows;

        // Each picture keeps its own colours, so a change of view can be a change of scheme.
        ShowLegend();

        Notes.HasViewNote = HasViewNote;

        // Whether a zoom is part of where the reader is turns on the view. See ExploreDisplay.Zooms.
        Visiting.Notify();
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
    /// The line under the map: what the pointer or the keyboard is on, how big and how old it is, and
    /// what Deguffer knows of it, or of the nearest folder above it that it knows anything about.
    ///
    /// <para>Nearest rather than exact, because a treemap draws a folder as a frame round its
    /// children and the pointer is nearly always on a file inside it. Asked exactly, the whole of
    /// <c>C:\Windows</c> answered nothing but that frame. The note says only what the reference says,
    /// and not the size or the date: those are on the line itself, where they can be read without
    /// waiting for the tooltip to appear.</para>
    /// </summary>
    [ObservableProperty]
    public partial MapReadout Readout { get; private set; } = MapReadout.None;

    public bool HasTree => Tree is not null;

    /// <summary>
    /// Whether to show the empty state instead of a picture. A large blank card reads as a screen
    /// that has failed rather than one waiting to be told to start.
    /// </summary>
    public bool HasNoTree => Tree is null;

    /// <summary>
    /// Whether the rows are being rewritten from here: this folder's (<see cref="ExploreRows.IsShowing"/>),
    /// or the Files layout's (<see cref="ExploreFiles.IsShowingRows"/>). The page reads it so a rewrite
    /// is not taken for a gesture on the list.
    /// </summary>
    public bool IsShowingRows => Listing.IsShowing || Files.IsShowingRows;

    /// <summary>
    /// Raised once the tree, the current node or the rows have changed, so the map redraws and the
    /// list's own selection is put back in step with <see cref="ExploreSelection.Nodes"/>. An event
    /// rather than the control watching several properties: a redraw is expensive and must happen
    /// once per change, not once per property that took part in it.
    /// </summary>
    public event EventHandler? ViewChanged;

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken ct)
    {
        if (Targeting.Target.Root is not { } target)
        {
            return;
        }

        // Read once, at the start. The scope can be changed while a scan runs, and a sentence
        // written afterwards would then describe the next scan rather than this one's result.
        var scoped = Targeting.IsScopedToFolder;
        var (scanned, volume) = Begin(target, ct);

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
                () => ExploreSurface.SnapshotOrder(_viewForSnapshots),
                ct);
            scanned.TrySetResult(scan);

            await FinishAsync(scan, volume, scoped);
        }
        catch (OperationCanceledException)
        {
            Abandon();
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
    /// Set the page up for a scan of <paramref name="target"/>: busy, with nothing compared, the
    /// refusal set re-read, the volume being read beside the scan, and the elevation offer back to what
    /// is known before anything is measured.
    /// </summary>
    /// <returns>
    /// What the refusal set waits on, which the scan completes with what it found, or with null where
    /// it ends without a scan; and the reading of the volume the scan is taken of.
    /// </returns>
    private (TaskCompletionSource<ExploreScan?> Scanned, ScannedVolume Volume) Begin(string target, CancellationToken ct)
    {
        IsBusy = true;
        Progress = null;
        Notes.RouteNote = null;

        // What is on screen is about to be replaced, and a comparison describes one tree.
        Growth.Clear();

        // Started with the scan and never awaited here. Part of §7.1's refusal set says what is
        // running right now, so it goes stale while the page is open, and a scan is the moment the
        // rest of the page is being re-measured anyway. It finishes once the scan does, reading what
        // the scan listed rather than listing it again beside it, so it is given the scan, or null
        // where the scan ends without one.
        var scanned = new TaskCompletionSource<ExploreScan?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Selection.Reconsider(ProtectionScope.Scanned(target, scanned.Task, _volumes));

        // Started with the scan so it runs while the scan does, rather than between its end and the map.
        var volume = ScannedVolume.Start(_volumes, _hidden, target, ct);

        // The previous scan's fallback reason is about to be replaced, and a cancelled or failed scan
        // never reaches the offer FinishAsync makes.
        Elevation.Offer(null, default);

        Status.Say(StatusLine.Said($"Scanning {target}…"));

        return (scanned, volume);
    }

    /// <summary>
    /// Show what a finished scan found, with the volume it was taken of, and clear the status line.
    /// </summary>
    /// <param name="volume">The reading of that volume, started with the scan.</param>
    /// <param name="scoped">Whether the scan was started on a folder rather than a drive.</param>
    private async Task FinishAsync(ExploreScan scan, ScannedVolume volume, bool scoped)
    {
        var space = await volume.SpaceAsync(scan.Tree);

        // Before the tree is shown, because the pointer can describe the new map from then on.
        _strategy = scan.Strategy;

        Show(scan.Tree, _position.CarriedTo(Tree, scan.Tree), space, ShowCause.Finished);

        Notes.RouteNote = scan.RouteNote;
        Elevation.Offer(scan.Fallback, space.Hidden);

        // The progress sentence goes, and nothing replaces it: the map, its readout and its rows
        // state the figures, and each one that is a lower bound is marked "≥". Before the scan is
        // kept, which waits for the list of volumes read beside it, so the sentence does not go on
        // describing a scan whose map is already drawn.
        Status.Say(StatusLine.Empty);

        await RecordAsync(scan, volume, scoped);
    }

    /// <summary>
    /// A cancelled scan: the last snapshot goes with it. A partial tree covers only the folders read
    /// so far, its <see cref="ExploreTree.HasUnknownSizes"/> is false because nothing refused
    /// anything, and it draws and navigates exactly like a finished scan — so leaving it on screen
    /// states a total for the drive that is wrong by however much was left. What grew was cleared as
    /// the scan began, and nothing compares a scan that never finished.
    /// </summary>
    private void Abandon()
    {
        Tree = null;
        Volume = VolumeSpace.None;
        Visiting.Notify();
        Selection.Show(null);
        Files.Show(null, 0);
        Types.Show(null, 0);
        Listing.Clear();
        ViewChanged?.Invoke(this, EventArgs.Empty);

        Status.Say(StatusLine.Said("Scan cancelled. Nothing was measured."));
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
    /// <param name="scanned">The reading of the volume the scan was taken of, which names it.</param>
    /// <param name="scoped">Whether the scan was started on a folder rather than a drive.</param>
    private async Task RecordAsync(ExploreScan scan, ScannedVolume scanned, bool scoped)
    {
        var space = Volume;
        var volume = await scanned.NameAsync();
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
        if (Tree is { } tree && Growth.Opens(tree, row))
        {
            Visiting.Visit(ExplorePosition.Inside(row.Node));
        }
    }

    /// <summary>
    /// What a screen reader is told of <paramref name="node"/> of <paramref name="tree"/> on the map: its
    /// name, whether it is a folder, and whether opening it shows what it holds (<see cref="Descend"/>).
    /// Nothing of a tree other than the one this page scanned, such as a step of a replay of what grew,
    /// whose node numbers mean nothing here.
    /// </summary>
    public MapShapeFacts? ShapeFacts(ISizedTree tree, int node) => MapShapeFacts.For(tree, Tree, node, this);

    /// <summary>
    /// Whether <paramref name="node"/> of <paramref name="tree"/> may be removed, and if not why (§7.1),
    /// for a screen reader to hear before Delete is pressed. Nothing of a tree other than the one this
    /// page scanned, as for <see cref="ShapeFacts"/>.
    ///
    /// <para>Apart from the facts because it asks the machine where the item's volume is mounted, each
    /// time it is asked, and a search of the window names every shape it meets.</para>
    /// </summary>
    public ExploreVerdict? ShapeRemoval(ISizedTree tree, int node) =>
        ShownTree.Of(tree, Tree, node) is { } scanned ? Selection.VerdictOf(scanned.PathOf(node)) : null;

    MapShapeFacts IMapShapes<ExploreTree>.FactsOf(ExploreTree tree, int node) =>
        new(tree.NameOf(node), ExploreNodeText.Kind(tree, node), _position.Opening(tree, node, Volume) is not null, tree.HasUnknownSizeBelow(node));

    MapReadout IMapShapes<ExploreTree>.ReadoutOf(ExploreTree tree, int node) => ExploreReadout.Over(tree, node, ReadoutState);

    string IMapShapes<ExploreTree>.NoteOn(ExploreTree tree, MapBlock block) => ExploreReadout.NoteOn(tree, block, ReadoutState);

    /// <summary>What the line under the map says beside the tree, read as it is now.</summary>
    private ExploreReadoutState ReadoutState => new(
        _time.GetUtcNow().UtcDateTime,
        SelectedColouring,
        Growth.Comparison,
        Types.Dominant,
        CurrentNode,
        _guide,
        Volume,
        _isElevated,
        _strategy);

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

        Visiting.Visit(opened);
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
            Visiting.Visit(up);
        }
    }

    /// <summary>Go straight to a step on the trail.</summary>
    public void GoTo(ExplorePosition position)
    {
        if (Tree is not null)
        {
            Visiting.Visit(position);
        }
    }

    /// <summary>
    /// Say what the pointer or the keyboard is on. Called from the map on every move to another shape,
    /// so it formats and assigns and does nothing else: anything heavier here runs at the display's
    /// refresh rate.
    /// </summary>
    public void Hover(ExploreHit? hit)
    {
        Readout = MapReadout.For(Tree, hit, this);

        // The card beside the map marks the row of what is pointed at, so the map answers "what" too.
        var pointed = hit is { IsNode: true } over ? over.Node : (int?)null;

        Types.Point(Tree, pointed);
        Growth.Point(Tree, pointed);
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
            Show(snapshot, _position.CarriedTo(Tree, snapshot), VolumeSpace.None, ShowCause.Snapshot);
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
    /// <param name="cause">Why, for a trace that times each show and its parts (<see cref="ShellEvents"/>).</param>
    private void Show(ExploreTree tree, ExplorePosition position, VolumeSpace volume, ShowCause cause)
    {
        var events = ShellEvents.Log;

        events.ShowStart(cause, tree.NodeCount);

        var redraw = ExploreRedraw.Between(Tree, _position, tree, position);

        // Before the tree is replaced, because the steps' node numbers belong to the one leaving.
        Visiting.Carry(Tree, tree);

        Tree = tree;
        Volume = volume;
        _position = position;

        OnPropertyChanged(nameof(CurrentNode));
        AscendCommand.NotifyCanExecuteChanged();
        Visiting.Notify();

        events.SelectionStart();

        if (redraw.KeepsSelection)
        {
            Selection.Carry(tree);
        }
        else
        {
            Selection.Show(tree);
        }

        events.SelectionStop();
        events.RowsStart();
        var listed = Listing.Show(tree, position, volume, redraw.KeepsRows);
        events.RowsStop(listed);
        Files.Show(tree, position.Node);
        Types.Show(tree, position.Node);
        FollowReplay();

        // What the pointer is over is the map's to say, and it says it again for the drawing this
        // redraw is about to produce. Clearing it here left a reader who had not moved with the
        // outline still round a shape and nothing under the map naming it.
        events.HandoffStart();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        events.HandoffStop();

        events.ShowStop(cause, listed);
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

        Show(left, _position.CarriedTo(removed.Tree, left), Volume.Remeasured(_volumes, left.RootPath), ShowCause.Removal);
    }

    private bool CanScan() => Gate.MayScan(Targeting.Target, _volumes);

    private bool CanAscend() => Tree is { } tree && _position.Up(tree, Volume) is not null;
}
