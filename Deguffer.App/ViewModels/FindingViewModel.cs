using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One row of the preview. §7: group by cause, sort by size, and state what happens on next use —
/// so this exposes the *sentence*, not just a checkbox and a number.
/// </summary>
public sealed partial class FindingViewModel : ObservableObject
{
    private readonly Finding _finding;

    public FindingViewModel(Finding finding)
    {
        _finding = finding;
        IsSelected = finding.HasReclaimableSpace && finding.Provider.Tier.IsPreSelectedByDefault();
    }

    /// <summary>Tier 1 is pre-selected; nothing else ever is (§3).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial CleanupResult? Result { get; set; }

    public Finding Finding => _finding;

    public string Name => _finding.Provider.Name;

    public string TierLabel => _finding.Provider.Tier.ToDisplayName();

    public string WhatHappensOnNextUse => _finding.Provider.WhatHappensOnNextUse;

    public string SizeLabel => _finding.IsPresent
        ? FreeSpace.Format(_finding.EstimatedBytes)
        : "—";

    public string StatusLabel => !_finding.IsPresent
        ? "Not installed on this machine"
        : _finding.HasReclaimableSpace
            ? "Ready to clean"
            : "Already clear";

    /// <summary>Only rows with something to reclaim can be acted on.</summary>
    public bool CanBeSelected => _finding.HasReclaimableSpace;

    public IReadOnlyList<string> Steps =>
        [.. _finding.Plan?.Steps.Select(s => s.Description) ?? []];

    public IReadOnlyList<string> Notes =>
        [.. _finding.Plan?.Notes.Select(n => n.Message) ?? []];

    /// <summary>§5.6, surfaced: the run has to show what it proved, not just what it removed.</summary>
    public string? VerificationSummary => Result?.Verification?.Summary;

    public bool HasResult => Result is not null;

    partial void OnResultChanged(CleanupResult? value)
    {
        OnPropertyChanged(nameof(VerificationSummary));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultLabel));
    }

    public string ResultLabel => Result is null
        ? string.Empty
        : $"Reclaimed {FreeSpace.Format(Result.BytesReclaimed)}" +
          (Result.SkippedCount > 0 ? $" · {Result.SkippedCount} item(s) in use were left alone" : string.Empty);
}
