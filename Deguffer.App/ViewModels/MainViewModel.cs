using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Drives the two-step flow of §7: Preview is the primary action and touches nothing; Clean is a
/// separate, explicit second step that only becomes available once a preview exists.
///
/// This type orchestrates and formats. It holds no knowledge of what any cache is or how to
/// remove it — that lives entirely in the providers (G2).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly CleanupPlanner _planner;

    public MainViewModel(CleanupPlanner? planner = null)
    {
        _planner = planner ?? CleanupPlanner.CreateDefault();
        FreeSpaceBefore = FreeSpace.ForUserProfile();
    }

    public ObservableCollection<FindingViewModel> Findings { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = "Preview to see what can be reclaimed. Nothing is removed until you say so.";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial long? FreeSpaceBefore { get; set; }

    [ObservableProperty]
    public partial long? FreeSpaceAfter { get; set; }

    /// <summary>Whether a preview exists, which is the only state from which cleaning is offered.</summary>
    [ObservableProperty]
    public partial bool HasPreview { get; set; }

    public string FreeSpaceBeforeLabel => Format(FreeSpaceBefore);

    public string FreeSpaceAfterLabel => Format(FreeSpaceAfter);

    public string ReclaimedLabel => FreeSpaceBefore is { } before && FreeSpaceAfter is { } after
        ? $"{FreeSpace.Format(after - before)} reclaimed"
        : string.Empty;

    /// <summary>The total the user is being offered, across selected rows only.</summary>
    public string SelectedTotalLabel => FreeSpace.Format(
        Findings.Where(f => f.IsSelected).Sum(f => f.Finding.EstimatedBytes));

    public bool CanClean => HasPreview && !IsBusy && Findings.Any(f => f.IsSelected);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task PreviewAsync(CancellationToken ct)
    {
        IsBusy = true;
        Findings.Clear();
        FreeSpaceAfter = null;

        try
        {
            FreeSpaceBefore = FreeSpace.ForUserProfile();

            var progress = new Progress<string>(message => Status = message);
            var findings = await _planner.PlanAllAsync(progress, ct);

            foreach (var finding in findings)
            {
                var row = new FindingViewModel(finding);
                row.PropertyChanged += OnRowChanged;
                Findings.Add(row);
            }

            HasPreview = true;
            Status = Findings.Any(f => f.CanBeSelected)
                ? $"{SelectedTotalLabel} can be reclaimed. Review the rows, then Clean."
                : "Nothing to reclaim — these caches are already clear.";
        }
        finally
        {
            IsBusy = false;
            RaiseDerived();
        }
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync(CancellationToken ct)
    {
        IsBusy = true;

        try
        {
            var selected = Findings.Where(f => f.IsSelected).ToList();
            var progress = new Progress<string>(message => Status = message);

            var results = await _planner.ExecuteAsync([.. selected.Select(f => f.Finding)], progress, ct);

            foreach (var result in results)
            {
                var row = selected.FirstOrDefault(f => f.Finding.Provider.Id == result.ProviderId);
                if (row is not null)
                {
                    row.Result = result;
                }
            }

            FreeSpaceAfter = FreeSpace.ForUserProfile();
            Status = BuildCompletionStatus(results);
            HasPreview = false;
        }
        finally
        {
            IsBusy = false;
            RaiseDerived();
        }
    }

    /// <summary>
    /// §5.6 is reported, not just performed. A verification failure is the headline, because it
    /// means a rule was over-broad and the user needs to know before the next run.
    /// </summary>
    private string BuildCompletionStatus(IReadOnlyList<CleanupResult> results)
    {
        var failures = results.Where(r => r.Verification is { Passed: false }).ToList();

        if (failures.Count > 0)
        {
            return $"Cleaned, but verification failed for {string.Join(", ", failures.Select(f => f.ProviderName))}. " +
                   "A protected path did not survive — please report this.";
        }

        var reclaimed = results.Sum(r => r.BytesReclaimed);
        return $"Reclaimed {FreeSpace.Format(reclaimed)}. All protected paths survived.";
    }

    private bool CanRun() => !IsBusy;

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FindingViewModel.IsSelected))
        {
            RaiseDerived();
        }
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(SelectedTotalLabel));
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(FreeSpaceBeforeLabel));
        OnPropertyChanged(nameof(FreeSpaceAfterLabel));
        OnPropertyChanged(nameof(ReclaimedLabel));
        PreviewCommand.NotifyCanExecuteChanged();
        CleanCommand.NotifyCanExecuteChanged();
    }

    private static string Format(long? bytes) => bytes is { } value ? FreeSpace.Format(value) : "—";
}
