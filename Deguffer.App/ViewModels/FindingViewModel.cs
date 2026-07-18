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
    public FindingViewModel(Finding finding)
    {
        Finding = finding;

        // Materialised once. These are bound per row, and rebuilding a list inside a property
        // getter puts an allocation on every binding evaluation.
        Notes = [.. finding.Plan?.Notes.Select(n => n.Message) ?? []];
        Steps = [.. finding.Plan?.Steps.Select(s => s.Description) ?? []];

        IsSelected = finding.IsPreSelectedByDefault;
    }

    /// <summary>§3's "Default" column decides this; the rule itself lives on <see cref="Finding"/>.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public Finding Finding { get; }

    public string Name => Finding.Provider.Name;

    public string TierLabel => Finding.Provider.Tier.ToDisplayName();

    public string WhatHappensOnNextUse => Finding.Provider.WhatHappensOnNextUse;

    public string SizeLabel => Finding.IsPresent ? FreeSpace.Format(Finding.EstimatedBytes) : "—";

    public string StatusLabel => !Finding.IsPresent
        ? "Not installed on this machine"
        : Finding.HasReclaimableSpace
            ? "Ready to clean"
            : "Already clear";

    /// <summary>Only rows with something to reclaim can be acted on.</summary>
    public bool CanBeSelected => Finding.HasReclaimableSpace;

    /// <summary>Exactly what would run — the plan, made inspectable before anything is deleted.</summary>
    public IReadOnlyList<string> Steps { get; }

    public IReadOnlyList<string> Notes { get; }

    public bool HasNotes => Notes.Count > 0;
}
