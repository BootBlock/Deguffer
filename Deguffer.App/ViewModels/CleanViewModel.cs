using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Sizes;
using Deguffer.Core.Execution.Timing;
using Deguffer.Core.Health;
using Deguffer.Core.Providers;
using Deguffer.Core.Retrim;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;
using Deguffer.Core.Working;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Drives the two-step flow of §7: Scan is the primary action and touches nothing; Clean is a
/// separate, explicit second step that only becomes available once a scan has run.
///
/// <para><b>"Scan" is what the user is told and "preview" is what the code calls the report it
/// produces.</b> The button said "Preview" beside one that said "Elevate and scan", which named one
/// action twice; every sentence the user reads now says scan. The type names below keep "preview"
/// because the thing they name is the plan, which is still a preview of a clean that has not
/// happened.</para>
///
/// This type orchestrates and formats. It holds no knowledge of what any cache is or how to
/// remove it — that lives entirely in the providers.
/// </summary>
public sealed partial class CleanViewModel : ObservableObject
{
    private readonly CleanupPlanner _planner;
    private readonly PlanNarrowing _narrowing;
    private readonly IUserEnvironment _environment;
    private readonly IVolumeInventory _volumes;
    private readonly bool _isElevated;
    private readonly Func<ElevationRequest, bool> _relaunch;
    private readonly SelectionService _selections;
    private readonly KeepService _keeps;
    private readonly RunningActions _running;
    private readonly BusyWork _work;
    private readonly Func<IConfirmationPrompt> _prompt;
    private readonly PlanTimeHistory _planTimes;
    private readonly SourceFolderAddition _sourceFolderAddition;

    /// <summary>The keep list the rows were last brought up to date with. See <see cref="ApplyKeepList"/>.</summary>
    private KeepList? _appliedKeepList;

    /// <summary>
    /// Whether the info bar is still showing the sentence the last preview put there.
    ///
    /// Cleared by <see cref="Report"/>, the one writer of <see cref="Status"/>, and set again by
    /// <see cref="ReportPreviewSummary"/>. That is what lets <see cref="OnSelectionChanged"/>
    /// keep the figures current without overwriting whatever else has since been said — a run
    /// outcome, a §5.6 verification failure, an error. Ticking a checkbox is not a reason to lose
    /// one of those.
    /// </summary>
    private bool _barShowsPreviewSummary;

    /// <summary>Stops the re-plan after a clean. See <see cref="ReplanRunChangesAsync"/>.</summary>
    private CancellationTokenSource? _replanCancellation;

    /// <summary>
    /// The tool the clean under way is waiting on, as the planner last reported it, so a Cancel pressed
    /// now can say what it will wait for. See <see cref="CleanCancelNotice"/>.
    /// </summary>
    private CommandUnderway? _underway;

    /// <summary>
    /// The locations the last scan measured, or null where it measured every one. The elevated
    /// relaunch measures them again: a user who scanned three chosen locations and asked to elevate
    /// asked for those three, not for every location, nor for whatever is ticked by then.
    /// </summary>
    private ProviderChoice? _lastChosen;

    /// <summary>How much of what Deguffer recognises the rows on screen were measured from. See <see cref="ScanCoverage"/>.</summary>
    private ScanCoverage _coverage;

    /// <param name="narrowing">Narrows each ticked row to its ticked steps when Clean is pressed.</param>
    /// <param name="volumes">Where the profile's volume is asked how full it is, before and after a run.</param>
    /// <param name="isElevated">
    /// Whether this process holds administrator rights. Read once by the page and handed down, so
    /// every row, step and offer on it is decided by the rights it was given rather than by
    /// whichever process's token a static read would find.
    /// </param>
    /// <param name="relaunch">Starts an elevated replacement, returning false where the user declined.</param>
    /// <param name="selections">
    /// What the rows were left ticked as last time, and where a change to that is written back.
    /// A scan re-plans from scratch, so without this every preview hands the user the same list of
    /// decisions to make again.
    /// </param>
    /// <param name="keeps">
    /// What the user keeps, which every row is built against and which the rows' own item lists
    /// change.
    /// </param>
    /// <param name="running">
    /// What is changing the machine on every page. A clean is recorded in it while it runs, and the
    /// page does not elevate while anything in it runs, here or on another page.
    /// </param>
    /// <param name="prompt">
    /// Deferred rather than injected directly: a dialog needs the page's <c>XamlRoot</c>, which does
    /// not exist while the view-model is being constructed.
    /// </param>
    /// <param name="build">Which build this is and what it runs on, which a run's diagnostic report names.</param>
    /// <param name="whenComplete">What follows a finished clean, chosen beside the Clean button.</param>
    /// <param name="retrim">The offer of Windows' retrim on the volumes a finished clean freed space on.</param>
    /// <param name="scanChoice">
    /// The locations the Scan button's drop-down scans on their own. Built over this page's planner,
    /// so its rows are the planner's own providers.
    /// </param>
    /// <param name="sizes">Each provider's figure at each scan, which every row states its change against.</param>
    /// <param name="driveHealth">What Health last read of each volume's drives. See <see cref="DriveHealth"/>.</param>
    /// <param name="sourceFolderAddition">How a folder the waiting rows ask for is approved, as Settings approves one.</param>
    public CleanViewModel(
        CleanupPlanner planner,
        PlanNarrowing narrowing,
        IUserEnvironment environment,
        IVolumeInventory volumes,
        bool isElevated,
        Func<ElevationRequest, bool> relaunch,
        SelectionService selections,
        KeepService keeps,
        RunningActions running,
        BusyWork work,
        Func<IConfirmationPrompt> prompt,
        BuildDetails build,
        WhenCompleteViewModel whenComplete,
        RetrimViewModel retrim,
        ScanChoiceViewModel scanChoice,
        ProviderSizeHistory sizes,
        HealthByVolume driveHealth,
        SourceFolderAddition sourceFolderAddition)
    {
        _planner = planner;
        _narrowing = narrowing;
        _environment = environment;
        _volumes = volumes;
        _isElevated = isElevated;
        _relaunch = relaunch;
        _selections = selections;
        _keeps = keeps;
        _running = running;
        _work = work;
        _prompt = prompt;
        _planTimes = new PlanTimeHistory(new PlanTimeStore(environment));
        _sourceFolderAddition = sourceFolderAddition;
        WhenComplete = whenComplete;
        Retrim = retrim;
        ScanChoice = scanChoice;
        Sizes = new StorageSizeChanges(sizes, Findings, isElevated);
        Selection = new StorageSelection(Findings, selections, isElevated, () => IsIdle && IsShowingFindings);
        Selection.Changed += OnSelectionChanged;

        // Asked of the machine for this one path, as the free space above is, rather than read from
        // the volume list, which may not have been read yet.
        DriveHealth = new DriveHealthLine(driveHealth, volumes.MountPointOf(environment.UserProfile));
        _coverage = new ScanCoverage(planner.Providers.Count, planner.Providers.Count);
        RunResult = new CleanRunResultViewModel(new ReportOrigin(build.Version, build.PlatformText, isElevated), environment);

        // Capacity cannot change while the app is open, so it is read once; only the free figure
        // is re-read after a run.
        var space = volumes.SpaceOf(environment.UserProfile);
        TotalSpace = space?.Total;
        FreeSpaceNow = space?.Free;

        _listed = new StorageListedRows(Findings);

        // Offered before anything has been scanned, so an elevated preview does not have to be
        // reached through the unelevated one it replaces.
        CanElevate = ElevationOffer.ShouldOffer(isElevated);

        // A clean or a removal on another page ends with this process as surely as one on this
        // page, so the button follows every page's actions and not only this one's busy flag.
        _running.Changed += (_, _) => ElevateAndRescanCommand.NotifyCanExecuteChanged();

        ScanChoice.ChoiceChanged += (_, _) => PreviewChosenCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Whether a clean whose rows need no answer of their own is still confirmed. The view sets it from
    /// the preference, so this type knows nothing about settings. It defaults to asking, so that a view
    /// which never sets it fails closed. A row §7 asks something of is confirmed whatever this says
    /// (<see cref="CleanConfirmation.MustAsk"/>).
    /// </summary>
    public bool ConfirmBeforeCleaning { get; set; } = true;

    /// <summary>
    /// Whether §7 holds Tier 3 to its typed phrase. The view sets it from the preference, the same
    /// way it sets <see cref="ConfirmBeforeCleaning"/>. It defaults to the strict rule so that a view
    /// which never sets it fails closed.
    ///
    /// With it false a Tier 3 row has no box of its own in the confirmation, which still lists it
    /// with what it loses.
    /// </summary>
    public bool RequireTypedConfirmation { get; set; } = true;

    /// <summary>
    /// How recently a file must have been touched for the user to want it left alone, in whole
    /// hours, or zero for no such guard. The view sets it from the preference, the same way it
    /// sets <see cref="ConfirmBeforeCleaning"/>.
    ///
    /// <para>The stored hours rather than the instant they become, and rather than a
    /// <see cref="TimeSpan"/>. The instant belongs to a preview rather than to a setting:
    /// <see cref="LoadPreviewAsync"/> asks <see cref="MinimumAge.WithinHours"/> for it once, at the
    /// top of the pass, so every provider protects the same files and the clean afterwards protects
    /// those same files again — however long the preview sat on screen first. Keeping it as hours is
    /// what puts that conversion behind the entry point that clamps, rather than in front of one
    /// that throws on a preferences file somebody edited by hand.</para>
    /// </summary>
    public int KeepFilesChangedWithinHours { get; set; }

    /// <summary>
    /// Whether to list a provider whose toolchain is not on this machine. The view sets it from the
    /// preference, the same way it sets <see cref="ConfirmBeforeCleaning"/>, so this type still
    /// knows nothing about settings.
    ///
    /// A hidden row is still scanned, still counted and still in the list — it is drawn or not.
    /// Skipping the provider instead would make the filter a decision about what Deguffer looks at,
    /// and a machine that gained the toolchain since the last scan would then be reported as not
    /// having it.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowNotInstalled { get; set; }

    /// <summary>
    /// Whether to list a location that is installed, readable and has nothing left to reclaim. Set
    /// from the preference by the view, exactly as <see cref="ShowNotInstalled"/> is, and hiding
    /// rather than skipping for the same reason: a row switched back on has to be there already.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowAlreadyClear { get; set; }

    /// <summary>
    /// How the rows are ordered. Set by the view from the preference and from the page's order picker,
    /// so this type still knows nothing about settings, and read back by it, because a value no order
    /// is named for is stored as the shipped one (<see cref="StorageRowOrder.Known"/>).
    ///
    /// <para>Changing it moves each row that is out of place, in the fewest moves, and never removes,
    /// re-creates or resets one, so every row keeps its ticks and its steps. A row that does not have to
    /// move keeps its container as well. It is the one thing that reorders a finished list: a row
    /// shrunk by keeping an item, or emptied by a clean, still stays where it stands.</para>
    /// </summary>
    public StorageOrder Order
    {
        get => _order;
        set
        {
            if (SetProperty(ref _order, StorageRowOrder.Known(value)))
            {
                var comparer = StorageRowOrder.Comparer(_order);
                LiveList.Show(Findings, (FindingViewModel[])[.. Findings.OrderBy(OrderedRowOf, comparer)], row => row);
            }
        }
    }

    private StorageOrder _order;

    public ObservableCollection<FindingViewModel> Findings { get; } = [];

    private readonly StorageListedRows _listed;

    /// <summary>The rows the list draws. See <see cref="StorageListedRows"/>.</summary>
    public BoundList<FindingViewModel> Listed => _listed.Rows;

    /// <summary>The rows waiting for a source folder, which the list folds into one group below it.</summary>
    public AwaitingFolderGroupViewModel AwaitingFolders => _listed.AwaitingFolders;

    public WhenCompleteViewModel WhenComplete { get; }

    /// <summary>The offer of Windows' retrim after a clean, and what the retrims it started answered.</summary>
    public RetrimViewModel Retrim { get; }

    public ScanChoiceViewModel ScanChoice { get; }

    /// <summary>Each row's change since the scan before, and each provider's own history.</summary>
    public StorageSizeChanges Sizes { get; }

    /// <summary>What Health last found wrong with the drive the free space figure is on.</summary>
    public DriveHealthLine DriveHealth { get; }

    /// <summary>What is ticked across the rows, and the menu entries that tick or clear them all.</summary>
    public StorageSelection Selection { get; }

    /// <summary>
    /// The row whose items are listed in place of the rows, or null while the rows are shown.
    ///
    /// <para>On this page rather than in a dialog, because choosing items is the main thing such a row
    /// is for, and Clean, which states what the ticks total, belongs beside the choice. What a run takes is still
    /// each row's own selection, so a clean started while the list is open is the same clean as one
    /// started from the rows.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingItems))]
    [NotifyPropertyChangedFor(nameof(IsShowingFindings))]
    public partial ItemListViewModel? ShownItems { get; private set; }

    public bool IsShowingItems => ShownItems is not null;

    /// <summary>The same fact the other way round, because x:Bind has no operators.</summary>
    public bool IsShowingFindings => ShownItems is null;

    partial void OnShownItemsChanged(ItemListViewModel? value) => Selection.RefreshMenu();

    /// <summary>
    /// List <paramref name="row"/>'s items in place of the rows.
    ///
    /// <para><b>Only for a row still on the page.</b> A row's dialog stays open while a preview replaces
    /// every row, and its button still holds the row it was opened for.
    /// A list for that row would take ticks the page no longer reads: nothing remembers them, Clean's
    /// figure ignores them, and Clean runs the row that replaced it. An item unticked there to keep it out
    /// of the next clean would still be deleted.</para>
    /// </summary>
    public void ShowItems(FindingViewModel row)
    {
        if (!Findings.Contains(row))
        {
            return;
        }

        CloseItems();
        ShownItems = new ItemListViewModel(row);
    }

    /// <summary>Put the rows back.</summary>
    public void CloseItems()
    {
        ShownItems?.Dispose();
        ShownItems = null;
    }

    /// <summary>Empty before a scan, so the bar is closed while the empty state says what to do.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = string.Empty;

    public bool HasStatus => Status.Length > 0;

    /// <summary>How loudly to say it: a §5.6 failure never reads as routine progress, so icon and text carry it as well as colour (§6.5).</summary>
    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewChosenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ElevateAndRescanCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSourceFolderCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Whether a clean is running, as distinct from the busy state a preview shares. Only the
    /// clean has a knowable extent, so only the clean gets a bar; a preview keeps the ring, which
    /// is the honest shape for an operation that cannot say how much is left.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCleaning { get; set; }

    /// <summary>
    /// Whether Cancel has been pressed during the clean under way, which may go on for minutes after
    /// the press while a tool is left to finish. Down with <see cref="IsCleaning"/>, because the
    /// re-plan after the run is a second operation that Cancel can stop again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsCancellingClean { get; set; }

    /// <summary>The Cancel button's words, which read as already pressed once it has been.</summary>
    public string CancelLabel => IsCancellingClean ? "Cancelling…" : "Cancel";

    /// <summary>How far the clean under way has got.</summary>
    public CleanProgressViewModel CleanProgress { get; } = new();

    /// <summary>What the last clean did, which stands until the next scan or clean replaces it.</summary>
    public CleanRunResultViewModel RunResult { get; }

    partial void OnShowNotInstalledChanged(bool value) => RefilterRows();

    partial void OnShowAlreadyClearChanged(bool value) => RefilterRows();

    /// <summary>
    /// Re-apply both filters to every row, rather than to new ones only, because either can change
    /// under a list that is already built.
    /// </summary>
    private void RefilterRows()
    {
        foreach (var row in Findings)
        {
            row.Place = PlaceOf(row);
        }

        // The collection did not change, so the subscription above raises nothing. Without this a
        // filter that empties the list leaves the empty state collapsed behind it.
        _listed.Show();

        // Select all ticks only the rows a filter leaves on screen.
        Selection.RefreshMenu();
    }

    /// <summary>Where a row is drawn, unless a filter the user left on hides it. See <see cref="PreviewFilter"/>.</summary>
    private PreviewPlace PlaceOf(FindingViewModel row) => new PreviewFilter(ShowNotInstalled, ShowAlreadyClear).Place(row.Status);

    /// <summary>Whether a preview exists — the only state from which cleaning is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSourceFolderCommand))]
    [NotifyPropertyChangedFor(nameof(ElevateLabel))]
    public partial bool HasPreview { get; set; }

    /// <summary>
    /// The profile volume's free space. Everything that reads it is told when it changes: the figure,
    /// the capacity bar, and the run's move from the reading before it (<see cref="CleanRunResultViewModel.Track"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreeSpaceNowLabel), nameof(UsedPercent))]
    public partial long? FreeSpaceNow { get; set; }

    partial void OnFreeSpaceNowChanged(long? value) => RunResult.Track(value);

    public long? TotalSpace { get; }

    /// <summary>
    /// How full the volume is, for the capacity bar. The bar answers "how bad is it" at a glance,
    /// which the bare free-space figure never did — 40 GB free means nothing until you know
    /// whether the disk is 256 GB or 4 TB.
    /// </summary>
    public double UsedPercent => TotalSpace is > 0 && FreeSpaceNow is { } free
        ? 100.0 * (TotalSpace.Value - free) / TotalSpace.Value
        : 0;

    public string CapacityLabel => TotalSpace is { } total
        ? $"free of {FreeSpace.Format(total)}"
        : "free";

    /// <summary>
    /// Whether to offer a relaunch as administrator. §5.5 made the slow scan observable; without
    /// this the app diagnoses the problem and leaves the user to solve it by knowing to right-click
    /// the executable.
    ///
    /// <para>This says whether elevating would help, not whether the page is free to act on it —
    /// that is the command's own <c>CanExecute</c>. Keeping the two apart is what stops the button
    /// disappearing for the length of every scan and returning afterwards.</para>
    /// </summary>
    [ObservableProperty]
    public partial bool CanElevate { get; set; }

    /// <summary>What that button says. See <see cref="ElevationOffer.Label"/>.</summary>
    public string ElevateLabel => ElevationOffer.Label(HasPreview);

    public string FreeSpaceNowLabel => FreeSpaceNow is { } value ? FreeSpace.Format(value) : "—";

    /// <summary>What the card says in place of its rows when it draws none.</summary>
    public StorageEmptyState EmptyState => _listed.Empty;

    public bool CanClean => HasPreview && !IsBusy && Selection.IsAnyTicked;

    [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
    private Task PreviewAsync(CancellationToken ct) => ScanAsync(chosen: null, ct);

    /// <summary>
    /// Scan only the locations chosen in the Scan button's drop-down. The list is replaced as a full
    /// scan replaces it, so every row on screen was measured by this scan.
    /// </summary>
    /// <param name="choice">
    /// The locations to scan, or null for the ones ticked now. An elevated relaunch passes the ones
    /// the replaced window last scanned.
    /// </param>
    [RelayCommand(CanExecute = nameof(CanScanChosen), IncludeCancelCommand = true)]
    private Task PreviewChosenAsync(ProviderChoice? choice, CancellationToken ct) =>
        ScanAsync(choice ?? ScanChoice.Choice, ct);

    private bool CanScanChosen(ProviderChoice? choice) =>
        CanRun() && (choice ?? ScanChoice.Choice).Of(_planner.Providers).Count > 0;

    /// <param name="chosen">The locations to scan, or null for every one.</param>
    private async Task ScanAsync(ProviderChoice? chosen, CancellationToken ct)
    {
        IsBusy = true;
        var work = _work.Begin();

        try
        {
            // A previous run's figures describe a machine state this preview is about to replace.
            ClearRunResult();

            await LoadPreviewAsync(chosen, ct);

            ReportPreviewSummary();
        }
        catch (OperationCanceledException)
        {
            Report("Scan cancelled. Nothing was changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A provider failing must not take the window down — this app's entire premise is
            // being trustworthy around deletion, and a crash is the worst available outcome.
            Report($"Scan failed: {ex.Message}", InfoBarSeverity.Error);
            work.Fail();
        }
        finally
        {
            IsBusy = false;
            work.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanClean), IncludeCancelCommand = true)]
    private async Task CleanAsync(CancellationToken ct)
    {
        // The ticks are read on the UI thread and each row is narrowed off it, because every declined
        // subject is a content walk and a row can decline thousands. The confirmation below is asked
        // of these narrowed rows, which are the ones that execute.
        List<Finding> selected;

        // Busy while the walks run, so no scan or keep can change the rows under the narrowing (a step
        // kept now would still be in the narrowed plan), and Cancel can stop it.
        IsBusy = true;

        try
        {
            // Narrowed again where a tick changed while the walks ran, so what runs is what the page
            // shows ticked. See TickedRows.
            TickedRows ticked;
            IReadOnlyList<CleanupPlan> narrowed;

            do
            {
                ticked = TickedRows.Of(Findings);
                narrowed = await _narrowing.NarrowAsync(ticked.Choices, ct);
            }
            while (!ticked.AreCurrent(Findings));

            selected = ticked.With(narrowed);
        }
        catch (OperationCanceledException)
        {
            Report("Clean cancelled. Nothing was removed.", InfoBarSeverity.Warning);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // Read once for the whole run. The preference can change under a live page — Settings is a
        // navigation away — and asking under one rule then executing under another would either
        // demand a phrase nobody was shown a box for, or skip an ask the run then relies on.
        var requireTypedPhrase = RequireTypedConfirmation;

        // One question for the whole clean, built in Core from every narrowed row: each row with its
        // size, what §7 asks of it, the true total, and what follows the clean. Asked with the page idle
        // again, before the run raises IsBusy, so cancelling leaves the screen as it was: no run result
        // is cleared and nothing is shown as under way. Cancel stops the whole clean; a row the user no
        // longer wants is unticked in the dialog instead.
        var confirmation = CleanConfirmation.For(selected, requireTypedPhrase, WhenComplete.Selected);
        var replies = confirmation.Opening;

        if (confirmation.MustAsk(ConfirmBeforeCleaning))
        {
            try
            {
                if (await _prompt().AskAsync(confirmation, ct) is not { } answered)
                {
                    return;
                }

                replies = answered;
            }
            catch (OperationCanceledException)
            {
                Report("Clean cancelled. Nothing was removed.", InfoBarSeverity.Warning);
                return;
            }
        }

        IsBusy = true;

        // Set only by a run that reached its §5.6 verdict. A clean that was declined, cancelled
        // before it started or failed has nothing for the choice beside the button to follow.
        RunOutcome? finished = null;

        // Ended with IsBusy rather than at the end of the method, so the countdown that may follow
        // the run is not shown as work under way.
        var work = _work.Begin();

        try
        {
            // A previous run's figures and its §5.6 verdict describe a machine state this run is
            // about to replace, exactly as a preview's do. Cleared here rather than when the new ones
            // arrive, because a run that fails never reaches RunResult.Record: leaving the old verdict
            // up would stand "Safety check passed" over a deletion nobody verified, which is the
            // inversion §5.6 exists to prevent. After the confirmation above, so cancelling it still
            // leaves the screen exactly as it was.
            ClearRunResult();

            var freeBefore = FreeSpaceOfProfile();

            // Every fixed volume, not only the profile's: a clean frees space wherever its rows were,
            // and a retrim is offered on each volume that freed enough.
            var volumesBefore = FreeSpaceReading.Take(_volumes);

            // Decided by Core from the answers, never by this type: an answer that does not satisfy a
            // ticked row throws here, before anything runs, and the planner checks every answer again.
            var (authorised, confirmations, declined) = confirmation.Authorise(replies);

            if (authorised.Count == 0)
            {
                // Distinguish declining from having nothing to decline: reporting a refused
                // confirmation to someone who was never asked for one describes the wrong event.
                // A refusal is a run that stopped, so it carries the same weight as a cancellation
                // rather than reading like routine progress.
                Report(
                    declined > 0
                        ? "Nothing was cleaned — no selected item was confirmed."
                        : "Nothing was cleaned — the selected items had nothing to remove.",
                    InfoBarSeverity.Warning);
                return;
            }

            // From the confirmation to the report, so the window will not close and no page will elevate
            // under the run. Not before the confirmation: until it is answered nothing has changed, and a
            // window closed over the dialog loses nothing.
            using var running = _running.Begin(RunningAction.StorageClean);

            // Every row this run will not clean still owes it the proof that its kept items and its
            // Outlook data files are standing afterwards (§5.6), and the planner runs that proof after
            // every deletion. Asked after the confirmation, because until it is answered nobody knows
            // which of the ticked rows will run. See StandingProof.
            var proving = StandingProof.For(Findings.Select(row => row.Finding), authorised);

            // A provider's "Cleaning…" line, posted before the press and delivered after it, would
            // otherwise put back the sentence the press replaced.
            var progress = new Progress<string>(message =>
            {
                if (!IsCancellingClean)
                {
                    Report(message);
                }
            });
            var underway = new Progress<CommandUnderway?>(command =>
            {
                _underway = command;

                // Said again as each tool starts and returns, because the press can arrive before the
                // report of the tool it will wait for.
                if (IsCancellingClean)
                {
                    ReportCancelling();
                }
            });
            var completed = new Progress<double>(fraction =>
            {
                CleanProgress.Advance(fraction);
                work.Report(fraction);
            });

            // The volume is read again as each row lands, so the bar under the figure drains by what
            // the disk actually gave back. Read rather than worked out from the count, because the two
            // differ whenever anything else writes during the run (§5.4).
            var landed = new Progress<CleanupResult>(result =>
            {
                CleanProgress.Count(result);
                FreeSpaceNow = FreeSpaceOfProfile();
            });

            var reporters = new CleanReporters { Status = progress, Progress = completed, Finished = landed, Underway = underway, Started = CleanProgress.Start(Findings) };
            IsCleaning = true;

            // The planner is started whatever the token says, and never throws for it: a cancelled clean
            // returns what it did and its §5.6 verdict, which a Task.Run that never started would lose.
            IReadOnlyList<CleanupResult> results;
            try
            {
                results = await Task.Run(
                    () => _planner.ExecuteAsync(
                        (Finding[])[.. authorised, .. proving], confirmations, requireTypedPhrase, reporters, ct),
                    CancellationToken.None);
            }
            finally
            {
                // Down before the re-preview below, not in the outer finally with IsBusy: the bar
                // describes the run that has just ended, and leaving it up over a scan would show a
                // full bar against a status line counting providers.
                IsCleaning = false;
                IsCancellingClean = false;
                _underway = null;
                CleanProgress.Finish();

                // The re-plan that follows cannot say how far it has got.
                work.Report(null);
            }

            // The card takes the run's figures and its §5.6 verdict the moment the run ends, before
            // the rescan below: it is the durable record, and leaving it showing the previous run
            // for the length of a scan would describe a machine that no longer exists.
            var outcome = RunResult.Record(results, freeBefore, FreeSpaceOfProfile());
            await Retrim.OfferAsync(volumesBefore, ct);

            if (outcome.VerificationFailed)
            {
                work.Fail();
            }

            // Only the rows the run changed are planned again. Found on a worker, as it asks where each
            // location is, and never cancelled: a cancel here would read as a clean that removed nothing.
            Finding[] shown = [.. Findings.Select(row => row.Finding)];
            var stale = await Task.Run(() => RunChanges.Stale(shown, authorised, _volumes));

            await ReplanRunChangesAsync(stale, ct);

            // Last, so a re-plan's per-provider progress lines cannot be what the bar is left
            // showing. See ReportOutcome for which of the two sentences wins it.
            ReportOutcome(outcome);
            finished = outcome;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException
                                      or ConfirmationRequiredException)
        {
            // NotSupportedException still reaches here from PlanExecutor for an unrecognised step
            // type. ConfirmationRequiredException means this view-model failed to collect an answer
            // the planner then demanded: a bug rather than a user outcome, but the planner refusing
            // to delete is the correct half of it — so report it instead of crashing mid-deletion.
            Report($"Clean failed: {ex.Message}", InfoBarSeverity.Error);
            work.Fail();
        }
        finally
        {
            IsBusy = false;
            work.Dispose();
            FreeSpaceNow = FreeSpaceOfProfile();
        }

        // Last, once the page shows everything the run has to say: the countdown is put over it, and
        // a locked or sleeping machine comes back to it.
        if (finished is not null && await WhenComplete.FollowAsync(finished, ct) is { } refusal)
        {
            Report(refusal, InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Planning enumerates directories synchronously before its first await, so it goes on a
    /// worker — otherwise the window is frozen on a cold volume and the progress ring never spins.
    ///
    /// §5.5: rows appear as each provider finishes rather than all at once at the end. Both
    /// callbacks are <see cref="Progress{T}"/>, so the planner reports from the worker and they
    /// arrive here on the UI thread; the dispatcher runs them in the order they were posted, which
    /// is what lets the rows be built here and the totals in the continuation below.
    /// </summary>
    private async Task LoadPreviewAsync(ProviderChoice? chosen, CancellationToken ct)
    {
        // Read once, so the figure the sentence states is the list the planner was handed.
        var measured = chosen?.Of(_planner.Providers);
        _lastChosen = chosen;
        _coverage = new ScanCoverage(measured?.Count ?? _planner.Providers.Count, _planner.Providers.Count);

        // An item list belongs to a row that is about to be thrown away, and a tick in it would change
        // a row no run can reach.
        CloseItems();

        foreach (var row in Findings)
        {
            Selection.Forget(row);
        }

        Findings.Clear();

        // Nothing is ticked on an empty list. Totalled here and again as each row lands, rather than
        // once at the end of the pass: a scan that is cancelled or fails never reaches the end, and
        // Clean's figure would state the previous scan beside rows that do not hold it.
        UpdateSelectionTotal();

        // Back to what is known before anything is measured, not just reassigned at the end: a
        // preview that is cancelled or fails never reaches the assignment below, and the rows whose
        // fallback reasons the old offer was read from have already been thrown away.
        HasPreview = false;
        CanElevate = ElevationOffer.ShouldOffer(_isElevated);

        // The sentence the last preview stated describes rows that are being thrown away, so the
        // bar stops being one a selection change may refresh. Explicit for the same reason
        // HasPreview drops here rather than being reassigned at the end.
        _barShowsPreviewSummary = false;

        // Read on a worker from launch, and nearly always long before a scan gets here. Waited for
        // before the first row is built, so no row starts at its tier default because the file was
        // still being read.
        var memory = await _selections.Memory.WaitAsync(ct);

        var progress = new Progress<string>(message => Report(message));
        var found = new Progress<Finding>(finding => AddRowInOrder(finding, memory));

        // Fixed here, once, for the whole pass. See KeepFilesChangedWithinHours.
        var keep = MinimumAge.WithinHours(KeepFilesChangedWithinHours, DateTime.UtcNow);

        // Every scan is timed, and Core keeps only one that planned every row, so which count is its call.
        var timed = new PlanTimeLog();

        await Sizes.BeginAsync(ct);

        try
        {
            await Task.Run(
                () => measured is null
                    ? _planner.PlanAllAsync(keep, progress, found, timed, ct)
                    : _planner.PlanAsync(measured, keep, progress, found, timed, ct),
                ct);
        }
        finally
        {
            await Sizes.EndAsync();
        }

        HasPreview = true;
        CanElevate = ElevationOffer.ShouldOffer(_isElevated, Findings.Select(f => f.Finding));

        // Past the finally, which a failed or cancelled pass leaves by. On a worker: it writes the file.
        await Task.Run(() => _planTimes.RecordFullPass(_planner.Providers, timed.Times, _isElevated), CancellationToken.None);
    }

    /// <summary>
    /// Plan <paramref name="providers"/> again and write each new finding over its row where the row
    /// stands. The list is told nothing: no row leaves it, none joins it and none moves, so the reader
    /// keeps their place and their focus.
    ///
    /// <para>The preview is withdrawn while it runs, as <see cref="LoadPreviewAsync"/> withdraws it.
    /// Until the last of these rows lands, the list describes the disk partly before the run and
    /// partly after it, and a re-plan that is cancelled or fails leaves it that way. Clean stays off
    /// until a scan describes the whole of it again.</para>
    /// </summary>
    private async Task ReplanAsync(IReadOnlyList<ICleanupProvider> providers, CancellationToken ct)
    {
        HasPreview = false;
        CanElevate = ElevationOffer.ShouldOffer(_isElevated);
        _barShowsPreviewSummary = false;

        // Read before the preview built the rows this re-plans, so this does not wait.
        var memory = await _selections.Memory.WaitAsync(ct);

        // Each finding is written over the row its provider had when the pass began, found by the
        // provider: the one instance the planner reports it by. Looked up in this map rather than
        // searched for in the list as each finding lands, so a finding with no row is passed over. A
        // search that found no row would throw inside a progress callback, where the exception reaches
        // nobody and every figure the rest of the callback owed the page is silently left behind.
        var rows = Findings.ToDictionary<FindingViewModel, ICleanupProvider>(
            row => row.Finding.Provider, ReferenceEqualityComparer.Instance);

        var progress = new Progress<string>(message => Report(message));
        var found = new Progress<Finding>(finding =>
        {
            if (rows.TryGetValue(finding.Provider, out var row))
            {
                ReplanRow(row, finding, memory);
            }
        });

        // Fixed here, once, for the whole pass. See KeepFilesChangedWithinHours.
        var keep = MinimumAge.WithinHours(KeepFilesChangedWithinHours, DateTime.UtcNow);

        await Sizes.BeginAsync(ct);

        try
        {
            await Task.Run(() => _planner.PlanAsync(providers, keep, progress, found, ct: ct), ct);
        }
        finally
        {
            await Sizes.EndAsync();
        }

        HasPreview = true;
        CanElevate = ElevationOffer.ShouldOffer(_isElevated, Findings.Select(f => f.Finding));
    }

    /// <summary>
    /// The re-plan after a clean, of the rows it changed.
    ///
    /// <para>Under a token of its own, which <see cref="Cancel"/> stops, and which the clean's token
    /// stops too while that one is live. A clean the user cancelled, part-way or after its last
    /// deletion, has spent its token, and what it changed still has to be planned again. A re-plan
    /// measures whole trees, so pressing Cancel again has to work.</para>
    ///
    /// <para>A re-plan stopped that way leaves the preview withdrawn, as <see cref="ReplanAsync"/>
    /// says, so Clean stays off until a scan. The run's own sentence still stands, a §5.6 alarm
    /// included, which is why the cancellation is caught here rather than reported over it.</para>
    /// </summary>
    private async Task ReplanRunChangesAsync(IReadOnlyList<ICleanupProvider> providers, CancellationToken ct)
    {
        using var replan = ct.IsCancellationRequested
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        _replanCancellation = replan;

        try
        {
            await ReplanAsync(providers, replan.Token);
        }
        catch (OperationCanceledException) when (replan.IsCancellationRequested)
        {
            // Stopped by Cancel. The rows stay withdrawn, and the caller reports the run.
        }
        finally
        {
            _replanCancellation = null;
        }
    }

    /// <summary>
    /// §7: the rows waiting for a source folder offer to add one where they stand, and every row the
    /// approved folder answers is planned again in place (<see cref="SourceFolderChanges"/>), without a scan.
    ///
    /// <para>Only over a preview of the whole disk: the re-plan puts the preview back, and over rows a
    /// cancelled pass left part-way that would turn Clean on over plans no scan stands behind. Busy from
    /// the start, because the volumes are read again before the warning with no dialog up, and a scan or
    /// clean started then would run under the re-plan. A failed save plans nothing; a folder already
    /// approved since the scan still plans the rows, because they are what is out of date.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddSourceFolder), IncludeCancelCommand = true)]
    private async Task AddSourceFolderAsync(CancellationToken ct)
    {
        IsBusy = true;
        BusySpan? work = null;

        try
        {
            switch (await _sourceFolderAddition.AddAsync(ct))
            {
                case { Choice: SourceFolderChoice.NotOnDisk }:
                    Report(PickedFolder.NotOnDisk);
                    return;
                case { Choice: not SourceFolderChoice.Approved }:
                    return;
                case { Saved: SaveOutcome.Refused }:
                    Report(SettingSave.NoticeMessage, InfoBarSeverity.Error);
                    return;
            }

            work = _work.Begin();
            await ReplanAsync(SourceFolderChanges.Stale([.. Findings.Select(row => row.Finding)]), ct);
            ReportPreviewSummary();
        }
        catch (OperationCanceledException) when (work is not null)
        {
            Report("The folder is approved, but its search was cancelled, so some rows are not up to date. Scan to see everything it holds.", InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while the folder was still being asked for, so nothing was stored or changed.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // As a scan's failure is caught, for the same reason: a provider failing must not take the window down.
            Report("The folder is approved, but Deguffer could not finish searching it because something in it could not be read. Scan to try again.", InfoBarSeverity.Error);
            work?.Fail();
        }
        finally
        {
            IsBusy = false;
            work?.Dispose();
        }
    }

    private bool CanAddSourceFolder() => CanRun() && HasPreview;

    /// <summary>
    /// Write one new finding over its row, and bring the page's figures up to date with it at once
    /// rather than at the end of the pass, which a cancelled or failed re-plan never reaches.
    /// </summary>
    private void ReplanRow(FindingViewModel row, Finding finding, SelectionMemory memory)
    {
        // Closed as its steps are replaced, not once before the pass: the rows stay on the page while
        // it runs, so a row's list can be opened after the pass began. Left open, its ticks would land
        // on steps no run takes, and an item unticked there would still be deleted.
        if (ReferenceEquals(ShownItems?.Row, row))
        {
            CloseItems();
        }

        row.Replan(finding, memory, _keeps.Current);
        row.Place = PlaceOf(row);
        Sizes.Show(row);
        ScanChoice.Missing.Record(finding.Provider, row.Status);

        // The row's steps are new, and a re-plan runs while the keep list is not to be changed.
        AllowKeepListChanges(row, !IsBusy);

        // Nothing was inserted, so the subscription that follows the list raises nothing, and a row
        // the run emptied is now one the filters may hide.
        _listed.Show();
        UpdateShares();
        UpdateSelectionTotal();
    }

    /// <summary>
    /// Raised once a replacement process is running and this one should stand down. An event rather
    /// than a call to <c>Application.Exit</c> because deciding to elevate and ending the process are
    /// different jobs (G2), and the second belongs to whoever owns the window.
    /// </summary>
    public event EventHandler? ReplacedByElevatedInstance;

    /// <summary>
    /// §6.3: a process cannot grant itself rights it started without, so this starts a replacement
    /// and stands down. The new instance previews on launch: the user asked for a scan by pressing
    /// this, and landing them on an empty window to press Scan again would not be that.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanElevateNow))]
    private void ElevateAndRescan()
    {
        if (!_relaunch(_lastChosen is null ? ElevationRequest.Preview : new PreviewRequest(_lastChosen)))
        {
            Report(
                "Deguffer is still running without administrator rights, so scans measure by walking "
                + "directories and any step needing those rights cannot be carried out. Everything else "
                + "works exactly the same.",
                InfoBarSeverity.Warning);
            return;
        }

        ReplacedByElevatedInstance?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// §7: sort by size, or by the order the user chose. Inserting each row where it belongs keeps the
    /// list ordered while it is still filling, rather than letting it reshuffle under the user once
    /// the last provider lands.
    /// </summary>
    private void AddRowInOrder(Finding finding, SelectionMemory memory)
    {
        var row = new FindingViewModel(finding, memory, _keeps.Current, _isElevated);

        // Rows arrive as their providers finish, so each one is filtered as it lands rather than in a
        // pass at the end that a cancelled scan would never reach.
        row.Place = PlaceOf(row);
        Sizes.Show(row);

        // As each row lands rather than once at the end, so a scan that is cancelled part-way still
        // marks the locations it did find missing.
        ScanChoice.Missing.Record(finding.Provider, row.Status);

        Selection.Follow(row);

        // Rows arrive while a preview is running, when the keep list is not to be changed.
        AllowKeepListChanges(row, !IsBusy);

        Findings.Insert(StorageRowOrder.IndexFor(Findings.Select(OrderedRowOf), OrderedRowOf(row), _order), row);
        UpdateShares();

        // A row can start ticked, and it raises no selection change for that: nobody ticked it.
        UpdateSelectionTotal();
    }

    /// <summary>
    /// What <see cref="StorageRowOrder"/> places <paramref name="row"/> by. The row's own figure
    /// rather than the finding the scan reported, so a kept item does not place a row by space it will
    /// never offer.
    /// </summary>
    private OrderedRow OrderedRowOf(FindingViewModel row) =>
        new(row.Name, row.Finding.EstimatedBytes, row.Facts.Tier, _planner.PlaceOf(row.Finding.Provider));

    /// <summary>
    /// Each row's bar is drawn relative to the largest finding, so the biggest cause fills the bar
    /// and everything else is read against it. Recomputed on every insert because rows arrive one
    /// provider at a time (§5.5) and the largest is not known until the last one lands.
    ///
    /// The largest is asked for rather than taken from the first row. Keeping an item shrinks its row
    /// where it stands, and the list is not reshuffled under the user for that, so the first row is
    /// not always the largest.
    /// </summary>
    private void UpdateShares()
    {
        var largest = Findings.Count > 0 ? Findings.Max(f => f.Finding.EstimatedBytes) : 0;

        foreach (var row in Findings)
        {
            row.SharePercent = largest > 0 ? 100.0 * row.Finding.EstimatedBytes / largest : 0;
        }
    }

    /// <summary>
    /// What the info bar is left saying once the run and the re-plan behind it have both finished.
    ///
    /// <para>A verification failure keeps it. It means a rule was over-broad, and the user needs to
    /// know before the next run. The card states the same sentence, and marks it by colour and
    /// weight, but only the bar has an icon and a severity behind it — so only the bar is told apart
    /// from routine text without reading to the end of the line, and announced as a problem rather
    /// than read out as one more line of text.</para>
    ///
    /// <para>A protected path taken by something else keeps it too, as a warning rather than an
    /// error. It is not a fault to report, but it is the reason the run's figures describe a machine
    /// that moved while the preview sat on screen — and yielding to the fresh preview's totals would
    /// replace that explanation with the numbers it explains. A protected path Windows would not
    /// describe keeps it as a warning as well, because nothing else on the screen says it went
    /// unchecked.</para>
    ///
    /// <para>A cancelled run keeps it as a warning whatever its verdict, because the totals would not
    /// say that the clean stopped part-way.</para>
    ///
    /// <para>Every other outcome yields to those totals, which describe the list now on screen. That
    /// sentence is also the only one a selection change may keep current, so stating it here is what
    /// makes ticking a row after a clean move the bar rather than leave it describing a machine two
    /// operations ago.</para>
    /// </summary>
    private void ReportOutcome(RunOutcome outcome)
    {
        if (outcome.NeedsReporting)
        {
            Report(
                outcome.Statement,
                outcome.VerificationFailed ? InfoBarSeverity.Error : InfoBarSeverity.Warning);

            return;
        }

        ReportPreviewSummary();
    }

    private void Report(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        // Here rather than at each call site, so a message added later cannot forget to say that
        // the preview's sentence is no longer what the bar shows.
        _barShowsPreviewSummary = false;

        Status = message;
        StatusSeverity = severity;
    }

    /// <summary>
    /// One Cancel for the user, whichever operation is in flight. G4: a scan the user cannot
    /// abandon is a bug, and two separate cancel buttons is not a UI.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        // Said at once, rather than when the run returns: a tool left to finish can hold it for
        // minutes, and the page would go on reading as a clean in progress.
        if (IsCleaning && !IsCancellingClean)
        {
            IsCancellingClean = true;
            ReportCancelling();
        }

        if (PreviewCommand.IsRunning)
        {
            PreviewCancelCommand.Execute(null);
        }

        if (PreviewChosenCommand.IsRunning)
        {
            PreviewChosenCancelCommand.Execute(null);
        }

        if (CleanCommand.IsRunning)
        {
            CleanCancelCommand.Execute(null);
        }

        if (AddSourceFolderCommand.IsRunning)
        {
            AddSourceFolderCancelCommand.Execute(null);
        }

        _replanCancellation?.Cancel();
    }

    private bool CanCancel() => !IsCancellingClean;

    private void ReportCancelling() => Report(CleanCancelNotice.For(_underway), InfoBarSeverity.Warning);

    private void ClearRunResult()
    {
        Retrim.Clear();
        RunResult.Clear();
    }

    /// <summary>
    /// Whether a scan can start, and so whether the bar shows Scan or Cancel in its place. Scan is
    /// hidden rather than disabled while the page is busy: a split button's command disables only its
    /// primary half, and the template then draws that half's disabled text over the accent.
    /// </summary>
    public bool IsIdle => !IsBusy;

    private bool CanRun() => IsIdle;

    /// <summary>
    /// Elevating ends this process, so it waits for this page's own work and for every page's
    /// actions.
    /// </summary>
    private bool CanElevateNow() => !IsBusy && _running.MayEndProcess;

    /// <summary>
    /// What is left on the profile's volume now. Asked of the machine each time rather than
    /// remembered, because it is read either side of a run to say what the run changed.
    /// </summary>
    private long? FreeSpaceOfProfile() => _volumes.SpaceOf(_environment.UserProfile)?.Free;

    /// <summary>
    /// One change by the user. The bar states what is ticked, so it follows every tick rather than only
    /// the end of a preview, or its figure would describe ticks the reader has since changed.
    /// </summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        CleanCommand.NotifyCanExecuteChanged();

        if (_barShowsPreviewSummary)
        {
            ReportPreviewSummary();
        }
    }

    /// <summary>
    /// Keep one item, or stop keeping it, from its row's item list.
    ///
    /// <para>The list is then applied to every row rather than this one alone, so the page's totals
    /// and its sentence move with the row. A change that could not be saved is said out loud
    /// (<see cref="KeepSaveNotice"/>).</para>
    /// </summary>
    public void ToggleKeep(FindingViewModel row, StepViewModel step)
    {
        // The rule the disabled button presents, answered here as well because a click can land in
        // the moment a run starts. See OnIsBusyChanged.
        if (IsBusy || step.Identity is not { } identity)
        {
            return;
        }

        var providerId = row.Finding.Provider.Id;
        var keeping = !step.IsKept;

        var saved = keeping
            ? _keeps.Keep(new KeptItem(providerId, row.Name, identity))
            : _keeps.Release(providerId, identity.Key) is SaveOutcome.Saved;

        ApplyKeepList();

        if (!saved)
        {
            Report(KeepSaveNotice.For(keeping, identity.Name, _keeps.StoredListUnreadable), InfoBarSeverity.Warning);
        }
    }

    /// <summary>
    /// Bring every row up to date with the keep list, which the Settings page can change while this
    /// page is away.
    ///
    /// <para>Nothing is done where the list is the one already applied. A <see cref="KeepList"/> is
    /// replaced rather than changed, so the same instance means nothing has changed — which is every
    /// return to this page that did not pass through one, and re-deriving every row's plan on each of
    /// those is per-row work for no answer (G4).</para>
    /// </summary>
    public void ApplyKeepList()
    {
        if (ReferenceEquals(_keeps.Current, _appliedKeepList))
        {
            return;
        }

        _appliedKeepList = _keeps.Current;

        foreach (var row in Findings)
        {
            row.ApplyKeepList(_keeps.Current);
        }

        RefilterRows();
        UpdateShares();
        UpdateSelectionTotal();

        if (_barShowsPreviewSummary)
        {
            ReportPreviewSummary();
        }
    }

    /// <summary>
    /// No keep or release while a preview or a clean is running. The run in progress was built from
    /// the list as it stood, so a keep clicked mid-clean would say an item is protected while the plan
    /// already executing deletes it. A dialog left holding a row from before a rescan stays refused
    /// too, because only the rows on the page are allowed again. The selection menu is refused for
    /// the run too, so it never ticks a row a scan is still adding.
    /// </summary>
    partial void OnIsBusyChanged(bool value)
    {
        Selection.RefreshMenu();

        foreach (var row in Findings)
        {
            AllowKeepListChanges(row, !value);
        }
    }

    /// <summary>Pushed down to each step, because a step template cannot reach this page through x:Bind.</summary>
    private static void AllowKeepListChanges(FindingViewModel row, bool allowed)
    {
        foreach (var step in row.Steps)
        {
            step.CanChangeKeepList = allowed;
        }
    }

    /// <summary>States the preview's sentence, and records that the bar is now showing it.</summary>
    private void ReportPreviewSummary()
    {
        Report(PreviewSummary.For(
            Findings.Select(f => f.Status),
            Selection.SelectableTotal,
            Selection.Total,
            ElevateLabel,
            _coverage));

        // After Report, which clears it.
        _barShowsPreviewSummary = true;
    }

    private void UpdateSelectionTotal()
    {
        Selection.Retotal();
        CleanCommand.NotifyCanExecuteChanged();
    }
}
