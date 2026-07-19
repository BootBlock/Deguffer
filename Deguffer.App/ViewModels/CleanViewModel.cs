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
    private readonly Func<IConfirmationPrompt> _prompt;

    /// <param name="prompt">
    /// Deferred rather than injected directly: a dialog needs the page's <c>XamlRoot</c>, which does
    /// not exist while the view-model is being constructed.
    /// </param>
    public CleanViewModel(
        CleanupPlanner planner,
        IUserEnvironment environment,
        Func<IConfirmationPrompt> prompt)
    {
        _planner = planner;
        _environment = environment;
        _prompt = prompt;

        // Capacity cannot change while the app is open, so it is read once; only the free figure
        // is re-read after a run.
        TotalSpace = FreeSpace.TotalForPath(environment.UserProfile);
        FreeSpaceNow = FreeSpace.ForPath(environment.UserProfile);

        // Rows arrive one provider at a time and are cleared wholesale between runs; subscribing
        // covers both without every mutation site having to remember to raise this.
        Findings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoFindings));
    }

    /// <summary>
    /// Asks the user to confirm before anything is deleted, returning whether to go ahead. The
    /// view supplies it and decides *how* to ask; leaving it null means do not ask, which is how
    /// the preference is expressed without this type knowing settings exist.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmCleanAsync { get; set; }

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

    /// <summary>Whether a preview exists — the only state from which cleaning is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
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
    /// </summary>
    [ObservableProperty]
    public partial bool CanElevate { get; set; }

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
    /// </summary>
    public bool HasNoFindings => Findings.Count == 0;

    public bool CanClean => HasPreview && !IsBusy && Findings.Any(f => f.IsSelected);

    [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
    private async Task PreviewAsync(CancellationToken ct)
    {
        IsBusy = true;
        HasPreview = false;

        // A previous run's figures describe a machine state this preview is about to replace.
        ClearRunResult();

        try
        {
            await LoadPreviewAsync(ct);

            Report(
                Findings.Any(f => f.CanBeSelected)
                    ? $"{SelectedTotalLabel} can be reclaimed. Review the rows, then Clean."
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

        // The blanket confirmation covers exactly what §7 will not ask about — nothing more, so no
        // deletion is confirmed twice, and nothing less, so no deletion goes unconfirmed. Standing
        // it down for the whole selection whenever any one row happened to be Tier 2 meant a mixed
        // selection deleted its Tier 1 rows with no confirmation of any kind, including when the
        // user declined the one dialog they were shown.
        //
        // Asked before IsBusy is raised, so declining leaves the screen exactly as it was rather
        // than flickering through a busy state for an operation that never started.
        var unasked = ConfirmationRequirement.NotPromptedFor(selected, f => f.Plan);

        if (ConfirmCleanAsync is { } confirm && unasked.Count > 0 && !await confirm(Describe(unasked)))
        {
            return;
        }

        IsBusy = true;
        var freeBefore = FreeSpace.ForPath(_environment.UserProfile);

        try
        {
            // §7's confirmation is collected here, on the UI thread and before any work starts:
            // a dialog cannot be raised from the worker below, and asking mid-deletion would be
            // asking after the point the answer could still change anything.
            var (authorised, confirmations) = await CollectConfirmationsAsync(selected, ct);

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

            var results = await Task.Run(() => _planner.ExecuteAsync(authorised, confirmations, progress, ct), ct);

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
        CollectConfirmationsAsync(IReadOnlyList<Finding> selected, CancellationToken ct)
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

            var requirement = ConfirmationRequirement.For(plan);

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
            row.SelectionChanged -= UpdateSelectionTotal;
        }

        Findings.Clear();

        // Cleared up front, not just reassigned at the end: a preview that is cancelled or fails
        // never reaches the assignment below, and a stale offer would advertise a speed-up for a
        // scan whose rows have already been thrown away.
        CanElevate = false;

        var progress = new Progress<string>(message => Report(message));
        var found = new Progress<Finding>(AddRowInSizeOrder);

        await Task.Run(() => _planner.PlanAllAsync(progress, found, ct), ct);

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
    /// and stands down. The new instance re-previews on launch — the button says "rescan", and
    /// landing the user on an empty window to press Preview again would not be that.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ElevateAndRescan()
    {
        if (!ElevatedRelaunch.TryRelaunch())
        {
            Report(
                "Deguffer is still running without administrator rights, so scans use the slower "
                + "directory walk. Everything else works exactly the same.",
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
        var row = new FindingViewModel(finding);

        // One event for both directions: the row's own checkbox and any step within it. Subscribing
        // to PropertyChanged(IsSelected) alone would miss a step being unticked while the row stays
        // ticked, which is the ordinary case for per-item selection.
        row.SelectionChanged += UpdateSelectionTotal;

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
    /// What the confirmation prompt says is about to happen. Names the rows rather than only
    /// totalling them: "3 items, 12 GB" is not something a user can check, and this is the last
    /// point at which a mistaken selection can still be caught.
    /// </summary>
    /// <summary>
    /// Names every row this dialog is asking about and totals only those. The total is recomputed
    /// rather than reusing <see cref="SelectedTotalLabel"/>: in a mixed selection that figure
    /// includes the rows §7 asks about separately, and a dialog that quotes a number larger than
    /// the deletions it authorises is describing something the user is not being asked to approve.
    /// </summary>
    /// <remarks>
    /// Takes the already-narrowed findings, so its total is the selected bytes by construction —
    /// with per-item selection a ticked row may be contributing only some of its steps, and the
    /// reasoning above applies to that too: the dialog must not quote a figure larger than the
    /// deletions it is authorising.
    /// </remarks>
    private static string Describe(IReadOnlyList<Finding> findings)
    {
        var total = FreeSpace.Format(findings.Sum(f => f.EstimatedBytes));

        return $"{string.Join(", ", findings.Select(f => f.Provider.Name))} — {total} in total. "
             + "This cannot be undone.";
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
        Report(
            $"Removed {RemovedLabel}. All protected paths survived." +
            (skipped > 0 ? $" {skipped} item(s) in use were left alone." : string.Empty),
            InfoBarSeverity.Success);
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
    /// Sums the selected <em>steps</em> rather than the selected rows. With per-item selection a
    /// ticked row no longer implies its whole plan will run, so totalling the finding would promise
    /// back space that the unticked steps within it are not going to release.
    /// </summary>
    private void UpdateSelectionTotal()
    {
        SelectedTotalLabel = FreeSpace.Format(Findings.Sum(f => f.SelectedBytes));

        CleanCommand.NotifyCanExecuteChanged();
    }
}
