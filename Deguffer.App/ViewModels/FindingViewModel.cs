using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Choosing;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
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
    ///
    /// It also decides who raises <see cref="SelectionChanged"/>: whoever set it is the outermost
    /// change, so the inner writes stay quiet and the event fires once per thing the user did.
    /// Firing per step made one click on a row with forty workspaces forty separate events, which
    /// is forty recalculated totals and forty writes of the remembered selection to disk.
    /// </summary>
    private bool _syncingSelection;

    /// <summary>
    /// The finding as its provider planned it, before the keep list took anything out. Held so that
    /// releasing an item puts it back with its size and its age, without a rescan.
    /// </summary>
    private Finding _found;

    /// <summary>Whether this process holds administrator rights. See <see cref="StepChoice"/>.</summary>
    private readonly bool _isElevated;

    /// <param name="memory">
    /// What this row and its steps were last left ticked as. It answers per step as well as per
    /// row, so restoring a ticked row does not re-tick the individual workspaces the user had
    /// unticked inside it.
    /// </param>
    /// <param name="keepList">
    /// What the user keeps. Applied through <see cref="CleanupPlan.WithKeepList"/> before anything
    /// below reads the plan, so every figure, status and default this row states is about what it
    /// will actually offer.
    /// </param>
    /// <param name="isElevated">
    /// Whether this process holds administrator rights, which decides whether a step needing them can
    /// be ticked. Handed down by the page rather than read from the token here.
    /// </param>
    public FindingViewModel(Finding finding, SelectionMemory memory, KeepList keepList, bool isElevated)
    {
        _isElevated = isElevated;
        Load(finding, memory, keepList);
    }

    /// <summary>
    /// Take the plan the re-plan after a clean made for this row's provider, and stay the same row.
    ///
    /// <para>Written over rather than replaced, so the list keeps the row's container, its place and
    /// its focus: only the rows the run changed are planned again, and the rest of the list is not
    /// about anything new. The row is not moved to where its new size would sort it either, which is
    /// how a row shrunk by keeping an item already behaves: the list is not reshuffled under the user.</para>
    ///
    /// <para>Everything else is what a row built from <paramref name="finding"/> would state, ticks
    /// included: <paramref name="memory"/> has recorded every change the user made, so each surviving
    /// step comes back as it was left, and a step the run emptied comes back unticked because it can
    /// no longer be ticked.</para>
    /// </summary>
    public void Replan(Finding finding, SelectionMemory memory, KeepList keepList)
    {
        foreach (var step in Steps)
        {
            step.PropertyChanged -= OnStepChanged;
        }

        Load(finding, memory, keepList);

        // Every binding on the row reads the plan that has just been replaced.
        OnPropertyChanged(string.Empty);
    }

    [MemberNotNull(nameof(_found), nameof(Finding), nameof(Notes), nameof(FacetColumns), nameof(Steps), nameof(Text))]
    private void Load(Finding finding, SelectionMemory memory, KeepList keepList)
    {
        _found = finding;
        Finding = WithKeepList(finding, keepList);

        var provider = finding.Provider;
        var startsSelected = memory.RowStartsSelected(provider.Id, provider.Tier, Finding.IsPreSelectedByDefault);

        // Materialised once per plan. These are bound per row, and rebuilding a list inside a
        // property getter puts an allocation on every binding evaluation.
        Notes = NotesOf(Finding);
        var offered = OfferedSteps(Finding);

        // Worked out once across every step, so each row of the item list is handed its values in the
        // same column order as every other. See ItemColumns.
        var columns = ItemColumns.Of(finding.Plan?.Steps ?? []);
        FacetColumns = columns.Labels;

        Steps =
        [
            // A step that cannot be acted on starts unticked whatever the finding's default is:
            // its checkbox is disabled, so ticking it would leave the user a selection they have
            // no way to clear, and the row-level toggle skips it for the same reason.
            //
            // The condition is StepChoice's rather than a copy of it. Written out here it
            // was a copy, and it went stale the moment a second reason to disable a checkbox
            // arrived — a step needing administrator rights would have started ticked, rendered
            // disabled, and been skipped by the loop that clears the row.
            //
            // Every step the provider planned is listed, kept ones included, so a kept item is seen
            // and released from the same place it was kept. Which of them are kept is read off the
            // plan the keep list produced, so the matching rule stays in Core.
            .. finding.Plan?.Steps.Select(s => new StepViewModel(
                s,
                memory.StepStartsSelected(provider.Id, provider.Tier, s.SelectionKey, startsSelected),
                isKept: !offered.Contains(s),
                _isElevated)
            {
                FacetValues = columns.ValuesOf(s),
            }) ?? [],
        ];

        Text = new FindingRowText(Name, Steps.Count, finding.AwaitingSourceFolders);

        // Subscribed only once every step exists. Handing each step a callback in its constructor
        // re-entered this one — the first pre-selected step raised the change before Steps had been
        // assigned, and the roll-up dereferenced it. Rows for every pre-selected provider silently
        // failed to appear, because an exception in a Progress callback has nowhere to surface.
        foreach (var step in Steps)
        {
            step.PropertyChanged += OnStepChanged;
        }

        // Under the guard, because the steps already carry their own restored state and the setter
        // below would otherwise overwrite every one of them with this single value.
        //
        // Rolled up from the steps rather than taken from the row's own remembered value, which is
        // the invariant the rest of this type holds: a row is selected when a step in it is. The
        // remembered value would tick a row that cannot contribute anything — one whose every step
        // is disabled for want of administrator rights, or one with no plan at all because the
        // toolchain has gone since the choice was made. Both render with a ticked, disabled
        // checkbox that nothing can clear, and both enable Clean against nothing.
        _syncingSelection = true;
        IsSelected = Steps.Any(s => s.IsSelected);
        _syncingSelection = false;
    }

    /// <summary>
    /// Raised once when this row's contribution to the selected total changes, whether that came
    /// from the row's own checkbox or from one step within it. It carries the row because the
    /// listener has to know which one to remember.
    /// </summary>
    public event Action<FindingViewModel>? SelectionChanged;

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

    /// <summary>
    /// Whether this row is drawn at all. Owned by the parent for the same reason
    /// <see cref="SharePercent"/> is: it answers a question about the list, not about this finding.
    ///
    /// Hidden rather than never built, so switching the filter needs no rescan and the row keeps
    /// its place in the size order it was inserted at.
    /// </summary>
    [ObservableProperty]
    public partial bool IsListed { get; set; } = true;

    /// <summary>
    /// The finding with the keep list applied, which is what this row describes and what a run is
    /// built from. It changes when the keep list does; see <see cref="ApplyKeepList"/>.
    /// </summary>
    public Finding Finding { get; private set; }

    public string Name => Finding.Provider.Name;

    public SafetyTier Tier => Finding.Provider.Tier;

    public string TierLabel => Finding.Provider.Tier.ToDisplayName();

    /// <summary>
    /// What the badge's tier means, for the reader who has never opened the About page. §3's
    /// classification is the product, and a two-word chip states it without explaining it.
    /// </summary>
    public string TierExplanation => Finding.Provider.Tier.ToExplanation();

    public string WhatHappensOnNextUse => Finding.Provider.WhatHappensOnNextUse;

    /// <summary>
    /// Whose files this row is about and what they are for — the answer to the question in front of
    /// the one <see cref="WhatHappensOnNextUse"/> answers. Handed through whole rather than
    /// unpacked into four properties: the dialog is the only reader, and it wants all four.
    /// </summary>
    public ProviderDescription Description => Finding.Provider.Description;

    /// <summary>
    /// §3's verdict on this row, worded for somebody deciding whether to tick it. Read off the tier
    /// rather than off <see cref="Finding.IsPreSelectedByDefault"/>, which is the same decision
    /// narrowed by whether there is anything here to reclaim — an empty cache would otherwise be
    /// advised against on a day it happened to be empty.
    /// </summary>
    public string CleaningAdvice => Tier.ToCleaningAdvice();

    public string SizeLabel => Finding.IsPresent ? FreeSpace.Format(Finding.Reclaim) : "—";

    /// <summary>
    /// What this row is reporting, as the single value both its own label and the page's info bar
    /// are read off. See <see cref="FindingStatus"/> for why one value rather than two conditions,
    /// and <see cref="FindingStatusExtensions.ToStatus"/> for the order the states are asked in.
    /// </summary>
    public FindingStatus Status => Finding.ToStatus(_isElevated);

    public string StatusLabel => Status.ToStatusLabel();

    /// <summary>
    /// Only rows with a step that can actually be acted on. See <see cref="StepChoice.AnyCanBeSelected"/>,
    /// which asks it of the plan the keep list left, so it agrees with the steps' own checkboxes.
    /// </summary>
    public bool CanBeSelected => StepChoice.AnyCanBeSelected(Finding, _isElevated);

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
    public bool HasSizeToShow => Finding.HasSomethingToRemove;

    /// <summary>Exactly what would run — the plan, made inspectable before anything is deleted.</summary>
    public IReadOnlyList<StepViewModel> Steps { get; private set; }

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
        SelectedSteps.Aggregate(ScanSize.Zero, (total, step) => total + step.Step.Reclaim);

    /// <summary>
    /// The ceiling <see cref="SelectedSize"/> can reach: every step this row offers, ticked or not.
    ///
    /// Steps rather than <see cref="Finding.Reclaim"/>, and only the selectable ones, so the two
    /// figures the info bar states side by side count the same bytes. Taking the finding's own total
    /// would include a step whose checkbox is disabled, and the bar would then offer space that no
    /// amount of ticking can reach.
    /// </summary>
    public ScanSize SelectableSize => Steps
        .Where(s => s.CanBeSelected)
        .Aggregate(ScanSize.Zero, (total, step) => total + step.Step.Reclaim);

    /// <summary>
    /// Whether this row's steps are listed to be chosen one by one, which is what puts the link to the
    /// item list on the row. Declared by the provider rather than read off the count; see
    /// <see cref="StepGrain"/>.
    /// </summary>
    public bool OffersItems => Finding.Provider.Grain.OffersEachStep(Steps.Count);

    /// <summary>What this row says about itself in words that change only with its plan. See <see cref="FindingRowText"/>.</summary>
    public FindingRowText Text { get; private set; }

    /// <summary>
    /// The one step of a row that has nothing to choose between, which the Contents tab still states
    /// in full: §7 makes the plan inspectable before anything is deleted. Null for a row whose steps
    /// are listed as items, and for a row with none.
    /// </summary>
    public StepViewModel? SoleStep => !OffersItems && Steps.Count == 1 ? Steps[0] : null;

    public bool HasSoleStep => SoleStep is not null;

    /// <summary>The headings of the item list's facet columns, in order. Empty where no step carries a facet.</summary>
    public IReadOnlyList<string> FacetColumns { get; private set; }

    public IReadOnlyList<string> Notes { get; private set; }

    /// <summary>
    /// Bring this row up to date with a keep list that changed while it was on screen, from its own
    /// item list or from Settings while the page was away.
    ///
    /// <para>Re-derived from what the provider planned rather than edited in place, so a released item
    /// comes back with its size and its age, and the rules deciding what this row states run once, in
    /// Core, for a changed list exactly as for a new one.</para>
    ///
    /// <para>A kept item is unticked as it is kept, and comes back unticked when it is released:
    /// keeping it was the user's last word about it.</para>
    /// </summary>
    public void ApplyKeepList(KeepList keepList)
    {
        // Most rows' providers name no items at all, and those rows can hold hundreds of steps.
        if (keepList.KeysFor(_found.Provider.Id).Count == 0 && !Steps.Any(step => step.IsKept))
        {
            return;
        }

        var applied = WithKeepList(_found, keepList);
        var offered = OfferedSteps(applied);

        if (Steps.All(step => step.IsKept == !offered.Contains(step.Step)))
        {
            return;
        }

        Finding = applied;
        Notes = NotesOf(applied);

        _syncingSelection = true;

        foreach (var step in Steps)
        {
            step.IsKept = !offered.Contains(step.Step);

            if (step.IsKept)
            {
                step.IsSelected = false;
            }
        }

        IsSelected = Steps.Any(s => s.IsSelected);
        _syncingSelection = false;

        // Everything this row states is read off the plan that just changed, so every binding on it
        // is refreshed rather than a list of names that the next new property would be missing from.
        OnPropertyChanged(string.Empty);

        SelectionChanged?.Invoke(this);
    }

    private static Finding WithKeepList(Finding finding, KeepList keepList) => finding.Plan is { } plan
        ? finding with { Plan = plan.WithKeepList(keepList.KeysFor(finding.Provider.Id)) }
        : finding;

    /// <summary>
    /// The steps a plan still offers, as a set: a row can hold one step per workspace, and each of its
    /// steps is looked up in here.
    /// </summary>
    private static HashSet<CleanupStep> OfferedSteps(Finding finding) => [.. finding.Plan?.Steps ?? []];

    private static IReadOnlyList<string> NotesOf(Finding finding) =>
        [.. finding.Plan?.Notes.Select(n => n.Message) ?? []];

    /// <summary>
    /// What to remember about this row, so a later scan starts where the user left it.
    ///
    /// This states the row's steps; which of them are worth recording is
    /// <see cref="RememberedSelection.Of"/>'s rule, and it lives there so it can be held to a test.
    ///
    /// A method rather than a property because it builds a map every time it is asked, and the
    /// steps of a build-output row are one per workspace.
    /// </summary>
    public RememberedSelection ToRemembered() => RememberedSelection.Of(
        IsSelected,
        Steps.Select(s => (s.Step.SelectionKey, s.IsSelected, s.CanBeSelected)));

    /// <summary>
    /// Shown whenever there is anything to say — including for a tool with nothing to reclaim.
    /// A provider that decided to leave children alone under §5.2 has recorded *why*, and that
    /// reasoning is the audit trail; hiding it because the tool happens to be clean would throw
    /// away the most useful thing Deguffer knows about it.
    /// </summary>
    public bool HasDetail => Steps.Count > 0 || Notes.Count > 0;

    /// <summary>
    /// Whether the Contents tab has to say that there is nothing to list. Stated here rather than
    /// negated in the template for the reason <see cref="CannotBeSelected"/> is: x:Bind has no
    /// operators, and a converter for one "not" would be more machinery than the property it
    /// replaces.
    /// </summary>
    public bool HasNoDetail => !HasDetail;

    /// <summary>Ticking the row ticks everything in it; unticking it clears the lot.</summary>
    partial void OnIsSelectedChanged(bool value)
    {
        if (_syncingSelection)
        {
            // Written by the roll-up below, or by the constructor restoring what was remembered.
            // Whoever set the guard reports the change once it is finished.
            return;
        }

        SetSelected(Steps, value);
    }

    /// <summary>
    /// Tick or clear several steps as one thing the user did: the row's own checkbox, or a checkbox in
    /// the item list that stands for a group or for everything a search shows. A step that cannot be
    /// ticked is passed over, for the reasons <see cref="StepViewModel.CanBeSelected"/> gives.
    ///
    /// <para>One notification and one event, however many steps change. Each event recalculates the
    /// page's totals over every row and writes the remembered selection to disk, so a click on a group of
    /// a thousand items that set its steps one at a time would do both a thousand times.</para>
    /// </summary>
    public void SetSelected(IEnumerable<StepViewModel> steps, bool value)
    {
        _syncingSelection = true;

        foreach (var step in steps.Where(s => s.CanBeSelected))
        {
            step.IsSelected = value;
        }

        // Rolled up rather than set to the value, which is the invariant the rest of this type holds.
        IsSelected = Steps.Any(s => s.IsSelected);
        _syncingSelection = false;

        OnPropertyChanged(nameof(SelectedSize));
        SelectionChanged?.Invoke(this);
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
        if (_syncingSelection)
        {
            // One step of a change to several. Whoever set the guard raises the total and the event
            // once, when every step has landed: nothing repaints between two steps of one change,
            // so a notification per step bought nothing but work.
            return;
        }

        OnPropertyChanged(nameof(SelectedSize));

        _syncingSelection = true;
        IsSelected = Steps.Any(s => s.IsSelected);
        _syncingSelection = false;

        SelectionChanged?.Invoke(this);
    }
}
