using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public MainViewModel(CleanupPlanner planner, IUserEnvironment environment)
    {
        _planner = planner;
        _environment = environment;
        FreeSpaceNow = FreeSpace.ForPath(environment.UserProfile);
    }

    public ObservableCollection<FindingViewModel> Findings { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } =
        "Preview to see what can be reclaimed. Nothing is removed until you say so.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Whether a preview exists — the only state from which cleaning is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    public partial bool HasPreview { get; set; }

    [ObservableProperty]
    public partial long? FreeSpaceNow { get; set; }

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
            var progress = new Progress<string>(message => Status = message);

            var results = await Task.Run(() => _planner.ExecuteAsync(selected, progress, ct), ct);

            ReportOutcome(results, freeBefore);

            // Re-plan rather than keeping the old rows: their sizes and "Ready to clean" labels
            // describe a machine that no longer exists.
            await LoadPreviewAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Status = "Clean cancelled. Anything already removed stays removed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Status = $"Clean failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            FreeSpaceNow = FreeSpace.ForPath(_environment.UserProfile);
        }
    }

    /// <summary>
    /// Planning enumerates directories synchronously before its first await, so it goes on a
    /// worker — otherwise the window is frozen on a cold volume and the progress ring never spins.
    /// </summary>
    private async Task LoadPreviewAsync(CancellationToken ct)
    {
        var progress = new Progress<string>(message => Status = message);
        var findings = await Task.Run(() => _planner.PlanAllAsync(progress, ct), ct);

        foreach (var row in Findings)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Findings.Clear();

        foreach (var finding in findings)
        {
            var row = new FindingViewModel(finding);
            row.PropertyChanged += OnRowChanged;
            Findings.Add(row);
        }

        HasPreview = true;
        UpdateSelectionTotal();
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
