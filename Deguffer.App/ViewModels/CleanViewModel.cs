using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Drives the two-step flow of §7: Preview is the primary action and touches nothing; Clean is a
/// separate, explicit second step that only becomes available once a preview exists.
///
/// This type orchestrates and formats. It holds no knowledge of what any cache is or how to
/// remove it — that lives entirely in the providers.
/// </summary>
public sealed partial class CleanViewModel : ObservableObject
{
    private readonly CleanupPlanner _planner;
    private readonly IUserEnvironment _environment;
    private readonly SelectionService _selections;
    private readonly Func<IConfirmationPrompt> _prompt;

    /// <param name="selections">
    /// What the rows were left ticked as last time, and where a change to that is written back.
    /// A scan re-plans from scratch, so without this every preview hands the user the same list of
    /// decisions to make again.
    /// </param>
    /// <param name="prompt">
    /// Deferred rather than injected directly: a dialog needs the page's <c>XamlRoot</c>, which does
    /// not exist while the view-model is being constructed.
    /// </param>
    public CleanViewModel(
        CleanupPlanner planner,
        IUserEnvironment environment,
        SelectionService selections,
        Func<IConfirmationPrompt> prompt)
    {
        _planner = planner;
        _environment = environment;
        _selections = selections;
        _prompt = prompt;

        // Capacity cannot change while the app is open, so it is read once; only the free figure
        // is re-read after a run.
        TotalSpace = FreeSpace.TotalForPath(environment.UserProfile);
        FreeSpaceNow = FreeSpace.ForPath(environment.UserProfile);

        // Rows arrive one provider at a time and are cleared wholesale between runs; subscribing
        // covers both without every mutation site having to remember to raise this.
        Findings.CollectionChanged += (_, _) => NotifyEmptyStateChanged();

        // Offered before anything has been scanned, so an elevated preview does not have to be
        // reached through the unelevated one it replaces.
        CanElevate = ElevationOffer.ShouldOffer(ElevatedRelaunch.IsElevated);
    }

    /// <summary>
    /// Asks the user to confirm before anything is deleted, returning whether to go ahead. The
    /// view supplies it and decides *how* to ask; leaving it null means do not ask, which is how
    /// the preference is expressed without this type knowing settings exist.
    /// </summary>
    public Func<CleanConfirmation, Task<bool>>? ConfirmCleanAsync { get; set; }

    /// <summary>
    /// Whether §7 holds Tier 3 to its typed phrase. The view sets it from the preference, the same
    /// way it supplies <see cref="ConfirmCleanAsync"/>, so this type still knows nothing about
    /// settings. It defaults to the strict rule so that a view which never sets it fails closed.
    ///
    /// With it false a Tier 3 row asks nothing of its own and is covered by the blanket
    /// confirmation instead, which <see cref="ConfirmationRequirement.NotPromptedFor"/> includes it
    /// in.
    /// </summary>
    public bool RequireTypedConfirmation { get; set; } = true;

    /// <summary>
    /// How recently a file must have been touched for the user to want it left alone, in whole
    /// hours, or zero for no such guard. The view sets it from the preference, the same way it
    /// supplies <see cref="ConfirmCleanAsync"/>.
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
    /// preference, the same way it supplies <see cref="ConfirmCleanAsync"/>, so this type still
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

    public ObservableCollection<FindingViewModel> Findings { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } =
        "Preview to see what can be reclaimed. Nothing is removed until you say so.";

    /// <summary>
    /// How loudly to say it. A §5.6 verification failure and a routine progress message used to
    /// render identically, which is the one distinction on this screen that must never be missed.
    /// Severity is carried by the info bar's icon and text as well as its colour (§6.5).
    /// </summary>
    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ElevateAndRescanCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Whether a clean is running, as distinct from the busy state a preview shares. Only the
    /// clean has a knowable extent, so only the clean gets a bar; a preview keeps the ring, which
    /// is the honest shape for an operation that cannot say how much is left.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCleaning { get; set; }

    /// <summary>How far through that clean, 0 to 100.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleanPercentLabel))]
    public partial double CleanPercent { get; set; }

    /// <summary>
    /// The same figure in words, beside the bar. §6.5's rule for the backdrop is the same rule
    /// here: nothing may be readable only as a graphic.
    /// </summary>
    public string CleanPercentLabel => $"{CleanPercent:0}%";

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
            row.IsListed = IsListed(row);
        }

        // The collection did not change, so the subscription above raises nothing. Without this a
        // filter that empties the list leaves the empty state collapsed behind it.
        NotifyEmptyStateChanged();
    }

    /// <summary>
    /// A row is drawn unless a filter the user left on hides it. Both hide a row that offers no
    /// decision: one whose toolchain this machine does not have, and one with nothing left to
    /// reclaim. Neither hides a row that has something to say, so the two are asked together and
    /// a row has to pass both.
    /// </summary>
    private bool IsListed(FindingViewModel row) =>
        (ShowNotInstalled || !row.IsToolchainMissing)
        && (ShowAlreadyClear || !row.IsAlreadyClear);

    /// <summary>Whether a preview exists — the only state from which cleaning is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    [NotifyPropertyChangedFor(nameof(ElevateLabel))]
    public partial bool HasPreview { get; set; }

    /// <summary>
    /// The dependents are declared because they are what the screen actually binds to. Without
    /// them the figure was written after a clean and never repainted — the volume had changed and
    /// the headline number still described the machine as it was before the run.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreeSpaceNowLabel))]
    [NotifyPropertyChangedFor(nameof(UsedPercent))]
    public partial long? FreeSpaceNow { get; set; }

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

    /// <summary>
    /// §5.4: two different numbers, reported separately. What Deguffer measured itself removing,
    /// and how the volume's free space actually changed — they disagree whenever anything else on
    /// the machine writes during the run, and presenting them as one number invites distrust.
    /// </summary>
    [ObservableProperty]
    public partial string RemovedLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FreeSpaceChangeLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedTotalLabel { get; set; } = FreeSpace.Format(0);

    public string FreeSpaceNowLabel => FreeSpaceNow is { } value ? FreeSpace.Format(value) : "—";

    public bool HasRunResult => !string.IsNullOrEmpty(RemovedLabel);

    /// <summary>
    /// Whether to show the empty state instead of the list. On launch the list is a large blank
    /// card, which reads as a screen that has failed rather than one waiting to be told to start.
    ///
    /// <para>Asked of what is drawn rather than of what was found, because a filter can empty the
    /// list under a scan that found plenty. The commonest case is the one that follows success:
    /// clean everything, let the run re-plan, and every row is then either clear or absent — both
    /// hidden by default. Counting rows instead put a blank card at the end of a run that
    /// worked.</para>
    /// </summary>
    public bool HasNothingListed => !Findings.Any(f => f.IsListed);

    /// <summary>
    /// Whether that empty list is the filters' doing rather than an empty machine. The two need
    /// different words: one says what to press, and the other says why a scan that finished is
    /// showing nothing, and where to switch it back on.
    /// </summary>
    private bool IsHiddenByFilters => Findings.Count > 0 && HasNothingListed;

    public string EmptyStateTitle => IsHiddenByFilters ? "Every row is hidden" : "Nothing scanned yet";

    public string EmptyStateMessage => IsHiddenByFilters
        ? "The scan found locations, and the filters are hiding all of them. Tick “Show items not "
          + "installed” above, or switch on “Show items that are already clear” in Settings."
        : "Preview looks at the locations Deguffer recognises and reports what each one holds. It "
          + "reads only — nothing is removed until you choose to.";

    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(HasNothingListed));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    public bool CanClean => HasPreview && !IsBusy && Findings.Any(f => f.IsSelected);

    [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
    private async Task PreviewAsync(CancellationToken ct)
    {
        IsBusy = true;

        // A previous run's figures describe a machine state this preview is about to replace.
        ClearRunResult();

        try
        {
            await LoadPreviewAsync(ct);

            // Three outcomes, not two. A row can hold real space this process may not act on, which
            // is neither "ready" nor "already clear" — and reporting it as the latter contradicts
            // the rows underneath, which show the size and say what they need.
            Report(
                Findings.Any(f => f.CanBeSelected)
                    ? $"{SelectedTotalLabel} can be reclaimed. Review the rows, then Clean."
                    : Findings.Any(f => f.Finding.HasReclaimableSpace)
                        ? "Nothing here can be cleared without administrator rights. "
                          + $"Use {ElevateLabel}."
                        : "Nothing to reclaim — these caches are already clear.");
        }
        catch (OperationCanceledException)
        {
            Report("Preview cancelled. Nothing was changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A provider failing must not take the window down — this app's entire premise is
            // being trustworthy around deletion, and a crash is the worst available outcome.
            Report($"Preview failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClean), IncludeCancelCommand = true)]
    private async Task CleanAsync(CancellationToken ct)
    {
        var selectedRows = Findings.Where(f => f.IsSelected).ToList();

        // Narrowed to the ticked steps, once. SelectedFinding rebuilds the plan on every access, so
        // reading it again further down would hand the confirmation check a different instance from
        // the one that executes — equal by value today, but not a property to depend on silently.
        var selected = selectedRows.Select(f => f.SelectedFinding).ToList();

        // Read once for the whole run. The preference can change under a live page — Settings is a
        // navigation away — and asking under one rule then executing under another would either
        // demand a phrase nobody was shown a box for, or skip an ask the run then relies on.
        var requireTypedPhrase = RequireTypedConfirmation;

        // The blanket confirmation covers exactly what §7 will not ask about — nothing more, so no
        // deletion is confirmed twice, and nothing less, so no deletion goes unconfirmed. Standing
        // it down for the whole selection whenever any one row happened to be Tier 2 meant a mixed
        // selection deleted its Tier 1 rows with no confirmation of any kind, including when the
        // user declined the one dialog they were shown.
        //
        // Asked before IsBusy is raised, so declining leaves the screen exactly as it was rather
        // than flickering through a busy state for an operation that never started.
        var unasked = ConfirmationRequirement.NotPromptedFor(selected, f => f.Plan, requireTypedPhrase);

        if (ConfirmCleanAsync is { } confirm && unasked.Count > 0)
        {
            // NotPromptedFor keeps only the rows carrying a plan, so nothing is dropped here.
            var plans = unasked.Select(f => f.Plan).OfType<CleanupPlan>().ToList();

            if (!await confirm(CleanConfirmation.For(plans)))
            {
                return;
            }
        }

        IsBusy = true;
        var freeBefore = FreeSpace.ForPath(_environment.UserProfile);

        try
        {
            // §7's confirmation is collected here, on the UI thread and before any work starts:
            // a dialog cannot be raised from the worker below, and asking mid-deletion would be
            // asking after the point the answer could still change anything.
            var (authorised, confirmations) =
                await CollectConfirmationsAsync(selected, requireTypedPhrase, ct);

            if (authorised.Count == 0)
            {
                // Distinguish declining from having nothing to decline: reporting a refused
                // confirmation to someone who was never asked for one describes the wrong event.
                // A refusal is a run that stopped, so it carries the same weight as a cancellation
                // rather than reading like routine progress.
                Report(
                    selected.Any(f => f.Plan is { IsEmpty: false })
                        ? "Nothing was cleaned — no selected item was confirmed."
                        : "Nothing was cleaned — the selected items had nothing to remove.",
                    InfoBarSeverity.Warning);
                return;
            }

            var progress = new Progress<string>(message => Report(message));
            var completed = new Progress<double>(SetCleanProgress);

            CleanPercent = 0;
            IsCleaning = true;

            IReadOnlyList<CleanupResult> results;
            try
            {
                results = await Task.Run(
                    () => _planner.ExecuteAsync(
                        authorised, confirmations, requireTypedPhrase, progress, completed, ct),
                    ct);
            }
            finally
            {
                // Down before the re-preview below, not in the outer finally with IsBusy: the bar
                // describes the run that has just ended, and leaving it up over a scan would show a
                // full bar against a status line counting providers.
                IsCleaning = false;
            }

            ReportOutcome(results, freeBefore);

            // Re-plan rather than keeping the old rows: their sizes and "Ready to clean" labels
            // describe a machine that no longer exists.
            await LoadPreviewAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Report("Clean cancelled. Anything already removed stays removed.", InfoBarSeverity.Warning);
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
        }
        finally
        {
            IsBusy = false;
            FreeSpaceNow = FreeSpace.ForPath(_environment.UserProfile);
        }
    }

    /// <summary>
    /// Ask for whatever §7 requires of each selection, and return only the ones that got an answer.
    ///
    /// Declining is a decision rather than a failure: that provider is dropped and the rest of the
    /// run continues, the same way a dismissed UAC prompt leaves the app running unelevated.
    /// </summary>
    private async Task<(List<Finding> Authorised, List<Confirmation> Confirmations)>
        CollectConfirmationsAsync(
            IReadOnlyList<Finding> selected,
            bool requireTypedPhrase,
            CancellationToken ct)
    {
        List<Finding> authorised = [];
        List<Confirmation> confirmations = [];
        IConfirmationPrompt? prompt = null;

        foreach (var finding in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (finding.Plan is not { IsEmpty: false } plan)
            {
                continue;
            }

            var requirement = ConfirmationRequirement.For(plan, requireTypedPhrase);

            if (requirement.Level == ConfirmationLevel.None)
            {
                authorised.Add(finding);
                continue;
            }

            // Built on first need, so a Tier 1 run never constructs a dialog it will not show.
            prompt ??= _prompt();

            if (await prompt.AskAsync(requirement, ct) is not { } answer)
            {
                continue;
            }

            authorised.Add(finding);
            confirmations.Add(answer);
        }

        return (authorised, confirmations);
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
    private async Task LoadPreviewAsync(CancellationToken ct)
    {
        foreach (var row in Findings)
        {
            row.SelectionChanged -= OnRowSelectionChanged;
        }

        Findings.Clear();

        // Back to what is known before anything is measured, not just reassigned at the end: a
        // preview that is cancelled or fails never reaches the assignment below, and the rows whose
        // fallback reasons the old offer was read from have already been thrown away.
        HasPreview = false;
        CanElevate = ElevationOffer.ShouldOffer(ElevatedRelaunch.IsElevated);

        var progress = new Progress<string>(message => Report(message));
        var found = new Progress<Finding>(AddRowInSizeOrder);

        // Fixed here, once, for the whole pass. See KeepFilesChangedWithinHours.
        var keep = MinimumAge.WithinHours(KeepFilesChangedWithinHours, DateTime.UtcNow);

        await Task.Run(() => _planner.PlanAllAsync(keep, progress, found, ct), ct);

        HasPreview = true;
        CanElevate = ElevationOffer.ShouldOffer(ElevatedRelaunch.IsElevated, Findings.Select(f => f.Finding));
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
    /// this, and landing them on an empty window to press Preview again would not be that.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ElevateAndRescan()
    {
        if (!ElevatedRelaunch.TryRelaunch(ElevationRequest.Preview))
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
    /// §7: sort by size. Inserting each row where it belongs keeps the list ordered while it is
    /// still filling, rather than letting it reshuffle under the user once the last provider lands.
    /// </summary>
    private void AddRowInSizeOrder(Finding finding)
    {
        var row = new FindingViewModel(finding, _selections.Memory);

        // Rows arrive one provider at a time, so each one is filtered as it lands rather than in a
        // pass at the end that a cancelled scan would never reach.
        row.IsListed = IsListed(row);

        // One event for both directions: the row's own checkbox and any step within it. Subscribing
        // to PropertyChanged(IsSelected) alone would miss a step being unticked while the row stays
        // ticked, which is the ordinary case for per-item selection.
        row.SelectionChanged += OnRowSelectionChanged;

        var index = 0;
        while (index < Findings.Count && Findings[index].Finding.EstimatedBytes >= finding.EstimatedBytes)
        {
            index++;
        }

        Findings.Insert(index, row);
        UpdateShares();
    }

    /// <summary>
    /// Each row's bar is drawn relative to the largest finding, so the biggest cause fills the bar
    /// and everything else is read against it. Recomputed on every insert because rows arrive one
    /// provider at a time (§5.5) and the largest is not known until the last one lands.
    ///
    /// The list is held in descending size order, so the first row is the reference.
    /// </summary>
    private void UpdateShares()
    {
        var largest = Findings.Count > 0 ? Findings[0].Finding.EstimatedBytes : 0;

        foreach (var row in Findings)
        {
            row.SharePercent = largest > 0 ? 100.0 * row.Finding.EstimatedBytes / largest : 0;
        }
    }

    /// <summary>
    /// §5.6 is reported, not just performed. A verification failure is the headline: it means a
    /// rule was over-broad, and the user needs to know before the next run.
    /// </summary>
    private void ReportOutcome(IReadOnlyList<CleanupResult> results, long? freeBefore)
    {
        var removed = results.Sum(r => r.BytesReclaimed);
        RemovedLabel = FreeSpace.Format(removed);

        var freeAfter = FreeSpace.ForPath(_environment.UserProfile);
        FreeSpaceChangeLabel = freeBefore is { } before && freeAfter is { } after
            ? FreeSpace.Format(after - before)
            : "—";

        OnPropertyChanged(nameof(HasRunResult));

        var failed = results.Where(r => r.Verification is { Passed: false }).ToList();
        if (failed.Count > 0)
        {
            Report(
                $"Cleaned, but verification failed for {string.Join(", ", failed.Select(f => f.ProviderName))}. " +
                "A protected path did not survive — please report this.",
                InfoBarSeverity.Error);
            return;
        }

        var skipped = results.Sum(r => r.SkippedCount);

        // Reported beside the skipped count and never folded into it. One is Windows refusing, which
        // the user can act on by closing something; the other is Deguffer honouring the setting they
        // chose. Saying nothing about the second leaves a run that reclaimed less than the preview
        // implied with no stated reason on screen at all.
        var kept = results.Sum(r => r.KeptCount);

        Report(
            $"Removed {RemovedLabel}. All protected paths survived." +
            (skipped > 0 ? $" {skipped} item(s) in use were left alone." : string.Empty) +
            (kept > 0 ? $" {kept} file(s) changed too recently to remove." : string.Empty),
            InfoBarSeverity.Success);
    }

    /// <summary>
    /// Takes a report from the run and moves the bar, but not for every one of them.
    ///
    /// A directory removal reports every 256 files, which on a fast volume is hundreds of reports a
    /// second, and each one that reaches the property raises a change notification and re-lays out
    /// the bar. Half a percent is under two pixels on any window this screen fits in, so anything
    /// smaller is spent for nothing. The end is always taken, because the one value the bar has to
    /// arrive at is the last one.
    /// </summary>
    private void SetCleanProgress(double fraction)
    {
        var percent = Math.Clamp(fraction * 100, 0, 100);

        if (percent >= 100 || percent - CleanPercent >= 0.5)
        {
            CleanPercent = percent;
        }
    }

    private void Report(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Status = message;
        StatusSeverity = severity;
    }

    /// <summary>
    /// One Cancel for the user, whichever operation is in flight. G4: a scan the user cannot
    /// abandon is a bug, and two separate cancel buttons is not a UI.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (PreviewCommand.IsRunning)
        {
            PreviewCancelCommand.Execute(null);
        }

        if (CleanCommand.IsRunning)
        {
            CleanCancelCommand.Execute(null);
        }
    }

    private void ClearRunResult()
    {
        RemovedLabel = string.Empty;
        FreeSpaceChangeLabel = string.Empty;
        OnPropertyChanged(nameof(HasRunResult));
    }

    private bool CanRun() => !IsBusy;

    /// <summary>
    /// One change by the user: retotal, and remember what they chose.
    ///
    /// Written on every change rather than at some tidier moment, because there is no reliable
    /// later one. A scan can be cancelled, a clean re-plans the list from scratch, and the process
    /// can be replaced outright by the elevated relaunch — all three would lose a choice that was
    /// only being held until the end.
    /// </summary>
    private void OnRowSelectionChanged(FindingViewModel row)
    {
        _selections.Remember(row.Finding.Provider.Id, row.ToRemembered());
        UpdateSelectionTotal();
    }

    /// <summary>
    /// Sums the selected <em>steps</em> rather than the selected rows. With per-item selection a
    /// ticked row no longer implies its whole plan will run, so totalling the finding would promise
    /// back space that the unticked steps within it are not going to release.
    /// </summary>
    private void UpdateSelectionTotal()
    {
        SelectedTotalLabel = FreeSpace.Format(
            Findings.Aggregate(ScanSize.Zero, (total, row) => total + row.SelectedSize));

        CleanCommand.NotifyCanExecuteChanged();
    }
}
