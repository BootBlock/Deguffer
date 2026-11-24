using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the Storage page's result card states about the last clean, for as long as its figures stand.
///
/// <para>Every decision is Core's: <see cref="RunFigures"/> for the figures, <see cref="RunOutcome"/>
/// for the verdict and whether it needs attention, <see cref="RunRows"/> for the line each location
/// gets, and <see cref="FreeSpaceMove"/> for the free space either side of the run. This type holds
/// what they said, so the page can bind to it, and forgets it when the next run begins.</para>
/// </summary>
public sealed partial class RunResultViewModel : ObservableObject
{
    /// <summary>The volume's free space read before the run, or null where no run's result is on screen.</summary>
    private long? _freeBefore;

    /// <summary>
    /// §5.4: what Deguffer measured itself removing. Reported apart from how the volume's free space
    /// moved (<see cref="FreeSpaceMoveText"/>), because they disagree whenever anything else on the
    /// machine writes during the run, and presenting them as one number invites distrust.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial string RemovedLabel { get; set; } = string.Empty;

    /// <summary>
    /// What sync apps were asked to release. Apart from the removed figure, because it is neither
    /// something Deguffer removed nor a change anybody measured. See <see cref="StepOutcome.BytesRequested"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequested))]
    public partial string RequestedLabel { get; set; } = string.Empty;

    public bool HasRequested => !string.IsNullOrEmpty(RequestedLabel);

    /// <summary>
    /// What a program marked to remove the next time it starts. Nothing has gone yet, so the free space
    /// cannot show it until the user opens that program. See <see cref="StepOutcome.BytesScheduled"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScheduled))]
    public partial string ScheduledLabel { get; set; } = string.Empty;

    public bool HasScheduled => !string.IsNullOrEmpty(ScheduledLabel);

    /// <summary>
    /// The free space now against the reading taken before the run, beside the hero figure (§7). Read
    /// against whatever the hero shows, so the two never disagree when a later reading moves it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFreeSpaceMove))]
    public partial string FreeSpaceMoveText { get; set; } = string.Empty;

    public bool HasFreeSpaceMove => !string.IsNullOrEmpty(FreeSpaceMoveText);

    /// <summary>
    /// The run's own §5.6 verdict, the heading over its rows.
    ///
    /// <para>The info bar cannot be its home. The bar describes whatever happened most recently, and
    /// what happens immediately after a clean is the re-plan that follows it, so a verdict left there
    /// is displaced by the next provider's progress line.</para>
    /// </summary>
    [ObservableProperty]
    public partial string Statement { get; set; } = string.Empty;

    /// <summary>
    /// Whether that verdict is a failure, so the card can say so in more than words. The words carry
    /// it on their own (§6.5).
    /// </summary>
    [ObservableProperty]
    public partial bool VerificationFailed { get; set; }

    /// <summary>
    /// Every §5.6 check the run has something to answer for, in full. A request to report a fault that
    /// does not say which path, why it was protected and what was found is a dead end.
    /// </summary>
    public ObservableCollection<VerificationCheck> Checks { get; } = [];

    public bool HasChecks => Checks.Count > 0;

    /// <summary>A line for each location the run cleaned. See <see cref="RunRow"/>.</summary>
    public ObservableCollection<RunRow> Rows { get; } = [];

    /// <summary>What the folded details hold. See <see cref="RunRows.Heading"/>.</summary>
    [ObservableProperty]
    public partial string RowsHeading { get; set; } = string.Empty;

    /// <summary>What the next scan does with a refusal. See <see cref="RunRows.Note"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRowsNote))]
    public partial string RowsNote { get; set; } = string.Empty;

    public bool HasRowsNote => !string.IsNullOrEmpty(RowsNote);

    /// <summary>
    /// Whether the details under the verdict are open. Folded after a run that went to plan, so the
    /// list of locations under the card keeps its room, and opened by every run whose sentence holds
    /// the info bar (<see cref="RunOutcome.NeedsReporting"/>). Two-way, because the reader opens and
    /// closes it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDetailsOpen { get; set; }

    /// <summary>
    /// The run written out for a GitHub issue, which the card's link copies. Empty where no run's
    /// figures are on screen. See <see cref="Core.Diagnostics.CleanRunReport"/>.
    /// </summary>
    public string Diagnostics { get; private set; } = string.Empty;

    /// <summary>Whether a run's result is on screen.</summary>
    public bool IsShown => !string.IsNullOrEmpty(RemovedLabel);

    /// <summary>State a finished run, with the free space read before it and as it stands now.</summary>
    public void Show(IReadOnlyList<CleanupResult> results, RunOutcome outcome, long? freeBefore, long? freeNow, string diagnostics)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var figures = RunFigures.For(results);
        var rows = RunRows.For(results);

        RequestedLabel = figures.Requested > 0 ? FreeSpace.Format(figures.Requested) : string.Empty;
        ScheduledLabel = figures.Scheduled > 0 ? FreeSpace.Format(figures.Scheduled) : string.Empty;

        _freeBefore = freeBefore;
        Track(freeNow);

        Statement = outcome.Statement;
        VerificationFailed = outcome.VerificationFailed;
        IsDetailsOpen = outcome.NeedsReporting;
        Diagnostics = diagnostics;

        // Written over rather than emptied and filled, so a second run that meets the same checks or
        // the same rows leaves them alone instead of taking them down and putting them back up.
        LiveList.Rewrite(Checks, figures.Checks);
        OnPropertyChanged(nameof(HasChecks));

        LiveList.Rewrite(Rows, rows.Rows);
        RowsHeading = rows.Heading;
        RowsNote = rows.Note;

        // Last, because the page brings the card in when this turns it on, and everything above is
        // what it brings in. FreeSpace states the entries only where no bytes went.
        RemovedLabel = FreeSpace.Format(figures.Removed);
    }

    /// <summary>Read the move again against a new reading of the free space.</summary>
    public void Track(long? freeNow) =>
        FreeSpaceMoveText = FreeSpaceMove.Between(_freeBefore, freeNow)?.Text ?? string.Empty;

    /// <summary>
    /// Forget the last run. A previous run's figures and verdict describe a machine state the next run
    /// is about to replace, and a verdict left up over a run that failed would stand "All protected
    /// paths survived" over a deletion nobody verified.
    /// </summary>
    public void Clear()
    {
        RemovedLabel = string.Empty;
        RequestedLabel = string.Empty;
        ScheduledLabel = string.Empty;
        _freeBefore = null;
        FreeSpaceMoveText = string.Empty;
        Statement = string.Empty;
        VerificationFailed = false;
        IsDetailsOpen = false;
        Diagnostics = string.Empty;
        Checks.Clear();
        OnPropertyChanged(nameof(HasChecks));
        Rows.Clear();
        RowsHeading = string.Empty;
        RowsNote = string.Empty;
    }
}
