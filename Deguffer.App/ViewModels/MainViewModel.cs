using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Drives the two-step flow of §7: Preview is the primary action and touches nothing; Clean is a
/// separate, explicit second step that only becomes available once a preview exists.
///
/// This type orchestrates and formats. It holds no knowledge of what any cache is or how to
/// remove it — that lives entirely in the providers.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly CleanupPlanner _planner;
    private readonly IUserEnvironment _environment;
    private readonly Func<IConfirmationPrompt> _prompt;

    /// <param name="prompt">
    /// Deferred rather than injected directly: a dialog needs the page's <c>XamlRoot</c>, which does
    /// not exist while the view-model is being constructed.
    /// </param>
    public MainViewModel(
        CleanupPlanner planner,
        IUserEnvironment environment,
        Func<IConfirmationPrompt> prompt)
    {
        _planner = planner;
        _environment = environment;
        _prompt = prompt;
        FreeSpaceNow = FreeSpace.ForPath(environment.UserProfile);
    }

    public ObservableCollection<FindingViewModel> Findings { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } =
        "Preview to see what can be reclaimed. Nothing is removed until you say so.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ElevateAndRescanCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Whether a preview exists — the only state from which cleaning is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    public partial bool HasPreview { get; set; }

    [ObservableProperty]
    public partial long? FreeSpaceNow { get; set; }

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

            Status = Findings.Any(f => f.CanBeSelected)
                ? $"{SelectedTotalLabel} can be reclaimed. Review the rows, then Clean."
                : "Nothing to reclaim — these caches are already clear.";
        }
        catch (OperationCanceledException)
        {
            Status = "Preview cancelled. Nothing was changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A provider failing must not take the window down — this app's entire premise is
            // being trustworthy around deletion, and a crash is the worst available outcome.
            Status = $"Preview failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClean), IncludeCancelCommand = true)]
    private async Task CleanAsync(CancellationToken ct)
    {
        IsBusy = true;
        var freeBefore = FreeSpace.ForPath(_environment.UserProfile);

        try
        {
            var selected = Findings.Where(f => f.IsSelected).Select(f => f.Finding).ToList();

            // §7's confirmation is collected here, on the UI thread and before any work starts:
            // a dialog cannot be raised from the worker below, and asking mid-deletion would be
            // asking after the point the answer could still change anything.
            var (authorised, confirmations) = await CollectConfirmationsAsync(selected, ct);

            if (authorised.Count == 0)
            {
                // Distinguish declining from having nothing to decline: reporting a refused
                // confirmation to someone who was never asked for one describes the wrong event.
                Status = selected.Any(f => f.Plan is { IsEmpty: false })
                    ? "Nothing was cleaned — no selected item was confirmed."
                    : "Nothing was cleaned — the selected items had nothing to remove.";
                return;
            }

            var progress = new Progress<string>(message => Status = message);

            var results = await Task.Run(() => _planner.ExecuteAsync(authorised, confirmations, progress, ct), ct);

            ReportOutcome(results, freeBefore);

            // Re-plan rather than keeping the old rows: their sizes and "Ready to clean" labels
            // describe a machine that no longer exists.
            await LoadPreviewAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Status = "Clean cancelled. Anything already removed stays removed.";
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
            Status = $"Clean failed: {ex.Message}";
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
            row.PropertyChanged -= OnRowChanged;
        }

        Findings.Clear();

        // Cleared up front, not just reassigned at the end: a preview that is cancelled or fails
        // never reaches the assignment below, and a stale offer would advertise a speed-up for a
        // scan whose rows have already been thrown away.
        CanElevate = false;

        var progress = new Progress<string>(message => Status = message);
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
            Status = "Deguffer is still running without administrator rights, so scans use the slower "
                   + "directory walk. Everything else works exactly the same.";
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
        row.PropertyChanged += OnRowChanged;

        var index = 0;
        while (index < Findings.Count && Findings[index].Finding.EstimatedBytes >= finding.EstimatedBytes)
        {
            index++;
        }

        Findings.Insert(index, row);
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
            Status = $"Cleaned, but verification failed for {string.Join(", ", failed.Select(f => f.ProviderName))}. " +
                     "A protected path did not survive — please report this.";
            return;
        }

        var skipped = results.Sum(r => r.SkippedCount);
        Status = $"Removed {RemovedLabel}. All protected paths survived." +
                 (skipped > 0 ? $" {skipped} item(s) in use were left alone." : string.Empty);
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

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FindingViewModel.IsSelected))
        {
            UpdateSelectionTotal();
        }
    }

    private void UpdateSelectionTotal()
    {
        SelectedTotalLabel = FreeSpace.Format(
            Findings.Where(f => f.IsSelected).Sum(f => f.Finding.EstimatedBytes));

        CleanCommand.NotifyCanExecuteChanged();
    }
}
