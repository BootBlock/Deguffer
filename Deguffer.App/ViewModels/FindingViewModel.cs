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
    /// <summary>
    /// Guards the two directions of the roll-up against each other. The row checkbox writes every
    /// step, and any step writes the row checkbox back; without this the first write re-enters and
    /// the second one fights it.
    /// </summary>
    private bool _syncingSelection;

    public FindingViewModel(Finding finding)
    {
        Finding = finding;

        // Materialised once. These are bound per row, and rebuilding a list inside a property
        // getter puts an allocation on every binding evaluation.
        Notes = [.. finding.Plan?.Notes.Select(n => n.Message) ?? []];
        Steps =
        [
            // A step with nothing to reclaim starts unticked whatever the finding's default is:
            // its checkbox is disabled, so ticking it would leave the user a selection they have
            // no way to clear, and the row-level toggle skips it for the same reason.
            .. finding.Plan?.Steps.Select(s => new StepViewModel(s, finding.IsPreSelectedByDefault && s.EstimatedBytes > 0)
            {
                // Only meaningful once the whole set is known, and a single step is the whole row.
                IsIndividuallySelectable = finding.Plan.Steps.Count > 1,
            }) ?? [],
        ];

        // Subscribed only once every step exists. Handing each step a callback in its constructor
        // re-entered this one — the first pre-selected step raised the change before Steps had been
        // assigned, and the roll-up dereferenced it. Rows for every pre-selected provider silently
        // failed to appear, because an exception in a Progress callback has nowhere to surface.
        foreach (var step in Steps)
        {
            step.PropertyChanged += OnStepChanged;
        }

        IsSelected = finding.IsPreSelectedByDefault;
    }

    /// <summary>
    /// Raised when this row's contribution to the selected total changes, whether that came from the
    /// row's own checkbox or from one step within it.
    /// </summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// §3's "Default" column decides the initial value; the rule itself lives on
    /// <see cref="Finding"/>. Toggling the row is a shorthand for toggling every step in it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>
    /// This row's size as a proportion of the largest finding, for the bar under the row. Owned by
    /// the parent because it is a fact about the *set*, not about this finding — the row cannot
    /// know what the biggest one is.
    /// </summary>
    [ObservableProperty]
    public partial double SharePercent { get; set; }

    public Finding Finding { get; }

    public string Name => Finding.Provider.Name;

    public SafetyTier Tier => Finding.Provider.Tier;

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
    public IReadOnlyList<StepViewModel> Steps { get; }

    /// <summary>
    /// This finding narrowed to the steps still selected, which is what actually gets executed.
    ///
    /// Narrowing goes through <see cref="CleanupPlan.NarrowedTo"/> rather than being done here,
    /// because that is what turns each deselected deletion into a protected path — §5.6's negative
    /// is the promise that a step the user unticked left its subject standing, and a shell that
    /// filtered the step list itself would drop that guarantee silently.
    /// </summary>
    public Finding SelectedFinding => Finding.Plan is { } plan
        ? Finding with { Plan = plan.NarrowedTo([.. SelectedSteps.Select(s => s.Step)]) }
        : Finding;

    public IReadOnlyList<StepViewModel> SelectedSteps => [.. Steps.Where(s => s.IsSelected)];

    /// <summary>What this row contributes to the selected total, counting only ticked steps.</summary>
    public long SelectedBytes => SelectedSteps.Sum(s => s.Step.EstimatedBytes);

    /// <summary>
    /// Whether the steps are individually worth choosing between. A single step <em>is</em> the
    /// whole finding, so offering a checkbox against it as well as against the row would put two
    /// controls on screen for one decision — and unticking either would visibly move the other.
    /// </summary>
    public bool HasSelectableSteps => Steps.Count > 1 && Steps.Any(s => s.CanBeSelected);

    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// Shown whenever there is anything to say — including for a tool with nothing to reclaim.
    /// A provider that decided to leave children alone under §5.2 has recorded *why*, and that
    /// reasoning is the audit trail; hiding it because the tool happens to be clean would throw
    /// away the most useful thing Deguffer knows about it.
    /// </summary>
    public bool HasDetail => Steps.Count > 0 || Notes.Count > 0;

    public string DetailHeader => Steps.Count > 0 ? "What this will do" : "What was left alone";

    /// <summary>Ticking the row ticks everything in it; unticking it clears the lot.</summary>
    partial void OnIsSelectedChanged(bool value)
    {
        if (!_syncingSelection)
        {
            _syncingSelection = true;

            foreach (var step in Steps.Where(s => s.CanBeSelected))
            {
                step.IsSelected = value;
            }

            _syncingSelection = false;
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// A row is selected when any step in it is. Unticking the last step clears the row rather than
    /// leaving it ticked with nothing to do, which would put a row in the run that removes nothing.
    /// </summary>
    private void OnStepChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StepViewModel.IsSelected))
        {
            OnStepSelectionChanged();
        }
    }

    private void OnStepSelectionChanged()
    {
        if (!_syncingSelection)
        {
            _syncingSelection = true;
            IsSelected = Steps.Any(s => s.IsSelected);
            _syncingSelection = false;
        }

        OnPropertyChanged(nameof(SelectedBytes));
        SelectionChanged?.Invoke();
    }
}
