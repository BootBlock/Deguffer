using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The card that states what the last clean did: its figures, the free space either side of it, its
/// §5.6 safety check with the report a GitHub issue can quote, and the line each location gets. It
/// stands from the moment the run ends until the next scan or clean replaces it, so it outlives the
/// re-plan that follows the run.
///
/// <para>Every decision is Core's: <see cref="RunFigures"/>, <see cref="FreeSpaceMove"/>,
/// <see cref="RunOutcome"/> and <see cref="RunRows"/>. This type holds what they said for the page.</para>
/// </summary>
/// <param name="origin">Which build ran the clean, and with what rights, which the report names.</param>
/// <param name="environment">The profile the report writes its paths against.</param>
public sealed partial class CleanRunResultViewModel(ReportOrigin origin, IUserEnvironment environment) : ObservableObject
{
    /// <summary>The volume's free space read before the run, or null where no run's result is on screen.</summary>
    private long? _freeBefore;

    /// <summary>
    /// §5.4: what Deguffer measured itself removing. Reported apart from how the volume's free space
    /// moved (<see cref="FreeSpaceMoveText"/>), because they disagree whenever anything else on the
    /// machine writes during the run, and presenting them as one number invites distrust.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunResult))]
    public partial string RemovedLabel { get; private set; } = string.Empty;

    /// <summary>
    /// §7: the free space now against the reading taken before the run, beside the hero figure. Read
    /// against whatever the hero shows (<see cref="Track"/>), so the two never disagree when a later
    /// reading moves it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFreeSpaceMove))]
    public partial string FreeSpaceMoveText { get; private set; } = string.Empty;

    public bool HasFreeSpaceMove => !string.IsNullOrEmpty(FreeSpaceMoveText);

    /// <summary>
    /// A third number, for a run that had one: what sync apps were asked to release. Apart from both
    /// above, because it is neither something Deguffer removed nor a change anybody measured. The
    /// release happens after the run, so the free space may not show it yet. See
    /// <see cref="StepOutcome.BytesRequested"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequested))]
    public partial string RequestedLabel { get; private set; } = string.Empty;

    public bool HasRequested => !string.IsNullOrEmpty(RequestedLabel);

    /// <summary>
    /// A fourth number, for a run that had one: what a program marked to remove the next time it
    /// starts. Apart from the others, because nothing has gone yet and the free space cannot
    /// show it until the user opens that program. See <see cref="StepOutcome.BytesScheduled"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScheduled))]
    public partial string ScheduledLabel { get; private set; } = string.Empty;

    public bool HasScheduled => !string.IsNullOrEmpty(ScheduledLabel);

    /// <summary>The run's own §5.6 verdict, beside its figures, for as long as those figures stand.</summary>
    public SafetyCheckViewModel SafetyCheck { get; } = new();

    public bool HasRunResult => !string.IsNullOrEmpty(RemovedLabel);

    /// <summary>A line for each location the run cleaned. See <see cref="RunRow"/>.</summary>
    public ObservableCollection<RunRow> Rows { get; } = [];

    /// <summary>What the folded lines hold. See <see cref="RunRows.Heading"/>.</summary>
    [ObservableProperty]
    public partial string RowsHeading { get; private set; } = string.Empty;

    /// <summary>What the next scan does with a refusal. See <see cref="RunRows.Note"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRowsNote))]
    public partial string RowsNote { get; private set; } = string.Empty;

    public bool HasRowsNote => !string.IsNullOrEmpty(RowsNote);

    /// <summary>
    /// Whether the lines are open. Folded after a run that went to plan, so the list of locations
    /// under the card keeps its room, and opened by every run whose verdict holds the info bar
    /// (<see cref="RunOutcome.NeedsReporting"/>). Two-way, because the reader opens and closes it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDetailsOpen { get; set; }

    /// <summary>
    /// §5.6 is reported, not just performed, so the run's verdict goes somewhere that outlives the
    /// run: this card, beside the figures it already carries.
    /// </summary>
    /// <param name="freeBefore">The profile volume's free space before the run, where it could be read.</param>
    /// <param name="freeAfter">The same, read once the run has ended. See <see cref="Track"/>.</param>
    public RunOutcome Record(IReadOnlyList<CleanupResult> results, long? freeBefore, long? freeAfter)
    {
        var figures = RunFigures.For(results);

        // FreeSpace states the entries only where no bytes went.
        RequestedLabel = figures.Requested > 0 ? FreeSpace.Format(figures.Requested) : string.Empty;
        ScheduledLabel = figures.Scheduled > 0 ? FreeSpace.Format(figures.Scheduled) : string.Empty;

        _freeBefore = freeBefore;
        Track(freeAfter);

        var outcome = RunOutcome.For(results);
        SafetyCheck.Show(outcome, figures.Checks, CleanRunReport.Describe(results, origin, environment));

        var rows = RunRows.For(results);

        // Filled where it stands, so the page's binding keeps the collection it was given.
        LiveList.Rewrite(Rows, rows.Rows);
        RowsHeading = rows.Heading;
        RowsNote = rows.Note;
        IsDetailsOpen = outcome.NeedsReporting;

        // Last, because it is what raises HasRunResult, and the page brings the card in on that: every
        // figure and the verdict are in place by then.
        RemovedLabel = FreeSpace.Format(figures.Removed);

        return outcome;
    }

    /// <summary>Read the move again against a new reading of the free space.</summary>
    public void Track(long? freeNow) =>
        FreeSpaceMoveText = FreeSpaceMove.Between(_freeBefore, freeNow)?.Text ?? string.Empty;

    /// <summary>Take the card down: its figures describe a machine state the next run replaces.</summary>
    public void Clear()
    {
        RemovedLabel = string.Empty;
        _freeBefore = null;
        FreeSpaceMoveText = string.Empty;
        RequestedLabel = string.Empty;
        ScheduledLabel = string.Empty;
        SafetyCheck.Clear();
        Rows.Clear();
        RowsHeading = string.Empty;
        RowsNote = string.Empty;
        IsDetailsOpen = false;
    }
}
