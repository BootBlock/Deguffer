using CommunityToolkit.Mvvm.ComponentModel;
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

    /// <param name="memory">
    /// What this row and its steps were last left ticked as. It answers per step as well as per
    /// row, so restoring a ticked row does not re-tick the individual workspaces the user had
    /// unticked inside it.
    /// </param>
    public FindingViewModel(Finding finding, SelectionMemory memory)
    {
        Finding = finding;

        var provider = finding.Provider;
        var startsSelected = memory.RowStartsSelected(provider.Id, provider.Tier, finding.IsPreSelectedByDefault);

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
            .. finding.Plan?.Steps.Select(s => new StepViewModel(
                s,
                memory.StepStartsSelected(provider.Id, provider.Tier, s.SelectionKey, startsSelected))
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

    public Finding Finding { get; }

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

    public string SizeLabel => Finding.IsPresent ? FreeSpace.Format(Finding.Estimated) : "—";

    /// <summary>
    /// What this row is reporting, as the single value both its own label and the page's info bar
    /// are read off. See <see cref="FindingStatus"/> for why one value rather than two conditions.
    ///
    /// <para>Presence is asked after <see cref="Finding.AwaitingSourceFolders"/>, because the two do
    /// not line up: the .NET build output is present whenever the SDK is, approved folders or not,
    /// and that row has as little to report as the four that are absent for the same reason.</para>
    ///
    /// <para>The held-back state asks the plan what the measurement actually withheld, never whether
    /// a guard is switched on. Driving the real window settled that: with the guard at seven days,
    /// deriving it from the setting put "Nothing old enough" on twelve rows, most of them simply
    /// empty — the same false claim wearing the opposite costume.</para>
    /// </summary>
    public FindingStatus Status => Finding.AwaitingSourceFolders
        ? FindingStatus.AwaitingSourceFolders
        : !Finding.IsPresent
        ? FindingStatus.ToolchainMissing
        : !Finding.HasReclaimableSpace
            ? Finding.Plan switch
            {
                { HasUnreadableRoot: true } => FindingStatus.UnreadableRoot,
                { WasNotExamined: true } => FindingStatus.NotExamined,
                { HasRecentContentHeldBack: true } => FindingStatus.RecentContentHeldBack,
                _ => FindingStatus.AlreadyClear,
            }
            : CanBeSelected
                ? FindingStatus.ReadyToClean
                : FindingStatus.NeedsElevation;

    public string StatusLabel => Status.ToStatusLabel();

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

    /// <summary>
    /// Whether this row is one the "show items not installed" filter hides.
    ///
    /// Only a tool that is genuinely not on this machine, never one waiting on a folder the user
    /// can approve. The second kind is absent in exactly the same way through
    /// <see cref="Finding.IsPresent"/>, and is the opposite of noise: it is the row that says the
    /// largest reclaimable thing on the disk is one setting away.
    /// </summary>
    public bool IsToolchainMissing => Status is FindingStatus.ToolchainMissing;

    /// <summary>
    /// Whether this row is one the "show items already clear" filter hides.
    ///
    /// Read off <see cref="Status"/> rather than restating the condition that produces it, because
    /// a second copy of that condition is free to disagree with the words on screen — and what this
    /// filter promises is that it hides exactly the rows saying "Already clear". The three
    /// neighbouring states measure zero as well and are not clear at all: a root Windows would not
    /// let Deguffer list, a location Deguffer declined to look at or could not locate, and a cache
    /// whose every file is inside the guard on recently changed files. All three stay listed,
    /// because each is a thing the user may want to act on.
    ///
    /// <para>A row this is true of can carry no ticked step, which is what makes hiding it safe:
    /// the label needs <see cref="Finding.HasReclaimableSpace"/> to be false, that is the sum of
    /// every step's reclaimable bytes, and no step is negative — so every step measures zero, and
    /// <see cref="StepViewModel.CanBeSelected"/> refuses each one. The proof holds only while both
    /// sides count the same bytes. Moving either to <c>ScanSize.Allocated</c> would break it, and a
    /// selected row would then be hidden by a filter that is on by default.</para>
    /// </summary>
    public bool IsAlreadyClear => Status is FindingStatus.AlreadyClear;

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

    /// <summary>
    /// What the Contents tab holds, named for which of the three things it is.
    ///
    /// A row with nowhere approved to look has left nothing alone: it has not looked. Calling its
    /// guidance "what was left alone" borrows §5.2's protected-path vocabulary for a sentence that
    /// is asking the user for something, and puts the only instruction on the screen behind a label
    /// that reads as a report.
    /// </summary>
    public string DetailHeader => Steps.Count > 0
        ? "What this will do"
        : Finding.AwaitingSourceFolders
            ? "What Deguffer needs"
            : "What was left alone";

    /// <summary>
    /// What a screen reader calls the compact row's disclosure. The whole row is that disclosure's
    /// header there, so it derives no name of its own and would otherwise be announced as an
    /// unnamed button.
    ///
    /// Named for the row rather than for what is inside it, because in the compact view the
    /// disclosure always holds the sentence §7 asks each row to state, whether or not there is a
    /// plan under it, and <see cref="DetailHeader"/> names only the plan half.
    /// </summary>
    public string DetailToggleName => $"More about {Name}";

    /// <summary>
    /// What a screen reader calls this row's information link. Every row's link reads "What is
    /// this?", so without a name of its own a reader hears the same three words down the whole list
    /// with nothing to tell one from another.
    /// </summary>
    public string InformationLinkName => $"What is {Name}?";

    /// <summary>Ticking the row ticks everything in it; unticking it clears the lot.</summary>
    partial void OnIsSelectedChanged(bool value)
    {
        if (_syncingSelection)
        {
            // Written by the roll-up below, or by the constructor restoring what was remembered.
            // Whoever set the guard reports the change once it is finished.
            return;
        }

        _syncingSelection = true;

        foreach (var step in Steps.Where(s => s.CanBeSelected))
        {
            step.IsSelected = value;
        }

        _syncingSelection = false;

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
        // Raised for every step, guard or no guard: the figure beside the row is bound to it, and
        // it moves as each step of a row-wide toggle lands.
        OnPropertyChanged(nameof(SelectedSize));

        if (_syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        IsSelected = Steps.Any(s => s.IsSelected);
        _syncingSelection = false;

        SelectionChanged?.Invoke(this);
    }
}
