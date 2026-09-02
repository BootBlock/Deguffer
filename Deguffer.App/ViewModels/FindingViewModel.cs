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
            // A step that cannot be acted on starts unticked whatever the finding's default is:
            // its checkbox is disabled, so ticking it would leave the user a selection they have
            // no way to clear, and the row-level toggle skips it for the same reason.
            //
            // The condition is StepViewModel's own rather than a copy of it. Written out here it
            // was a copy, and it went stale the moment a second reason to disable a checkbox
            // arrived — a step needing administrator rights would have started ticked, rendered
            // disabled, and been skipped by the loop that clears the row.
            .. finding.Plan?.Steps.Select(s => new StepViewModel(s, finding.IsPreSelectedByDefault)
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

    public string SizeLabel => Finding.IsPresent ? FreeSpace.Format(Finding.Estimated) : "—";

    public string StatusLabel => !Finding.IsPresent
        ? "Not installed on this machine"
        : !Finding.HasReclaimableSpace
            // "Already clear" is a claim, and it must not be made about a folder Windows would not
            // let Deguffer list. The expander below names which one and why the figure is short.
            ? Finding.Plan?.HasUnreadableRoot == true ? "Could not be read" : "Already clear"
            : CanBeSelected
                ? "Ready to clean"
                // Nothing in the row can be acted on as Deguffer is running, so "Ready to clean"
                // beside a disabled checkbox would contradict itself. The Windows servicing logs are
                // the whole row of this kind: every step of them sits under the Windows directory.
                : "Needs administrator rights";

    /// <summary>
    /// Only rows with a step that can actually be acted on.
    ///
    /// Asked of the steps rather than of the finding's total, because the row checkbox is a shorthand
    /// for ticking every step in it: where nothing in the row is selectable, ticking it would tick
    /// nothing and leave a row that says it is selected and removes nothing. That case arrived with
    /// the Windows servicing logs, every step of which needs administrator rights.
    /// </summary>
    public bool CanBeSelected => Finding.HasReclaimableSpace && Steps.Any(s => s.CanBeSelected);

    /// <summary>
    /// Whether the compact row states why it cannot be ticked. Stated here rather than negated in
    /// the template because x:Bind has no operators, and a converter for one "not" would be more
    /// machinery than the property it replaces.
    /// </summary>
    public bool CannotBeSelected => !CanBeSelected;

    /// <summary>
    /// Whether the compact row has a figure worth showing.
    ///
    /// Asked separately from <see cref="CannotBeSelected"/> because the two are independent, and
    /// treating them as opposites lost the size of the row most likely to be the largest. A row
    /// needing administrator rights has a real, measured total and still cannot be ticked, and the
    /// list is ordered by that total — so hiding it left the biggest cause at the top of the list
    /// with no number against it, which is the one figure that decides whether elevating is worth
    /// it. A row with nothing to reclaim shows "0 B" or "—", which the reason beside it already
    /// says in words.
    /// </summary>
    public bool HasSizeToShow => Finding.HasReclaimableSpace;

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
    public ScanSize SelectedSize =>
        SelectedSteps.Aggregate(ScanSize.Zero, (total, step) => total + step.Step.Estimated);

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

    /// <summary>
    /// What a screen reader calls the compact row's disclosure. The whole row is that disclosure's
    /// header there, so it derives no name of its own and would otherwise be announced as an
    /// unnamed button.
    ///
    /// Named for the row rather than for what is inside it, because in the compact view the
    /// disclosure always holds the sentence §7 asks each row to state, whether or not there is a
    /// plan under it — <see cref="DetailHeader"/> would call a not-installed row's description
    /// "what was left alone".
    /// </summary>
    public string DetailToggleName => $"More about {Name}";

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

        OnPropertyChanged(nameof(SelectedSize));
        SelectionChanged?.Invoke();
    }
}
