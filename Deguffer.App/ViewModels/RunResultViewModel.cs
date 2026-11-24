using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the Storage page's result card states about the last clean beside its §5.6 verdict, for as long
/// as its figures stand.
///
/// <para>Every decision is Core's: <see cref="RunFigures"/> for the figures, <see cref="RunRows"/> for
/// the line each location gets, <see cref="RunOutcome.NeedsReporting"/> for whether those lines open
/// on their own, and <see cref="FreeSpaceMove"/> for the free space either side of the run. This type
/// holds what they said, so the page can bind to it, and forgets it when the next run begins. The
/// verdict itself is <see cref="SafetyCheckViewModel"/>'s.</para>
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

    /// <summary>Whether a run's result is on screen.</summary>
    public bool IsShown => !string.IsNullOrEmpty(RemovedLabel);

    /// <summary>
    /// State a finished run: its figures, the line each location gets, whether the lines open on their
    /// own, and the free space read before it and as it stands now.
    /// </summary>
    public void Show(RunFigures figures, RunRows rows, bool opensDetails, long? freeBefore, long? freeNow)
    {
        ArgumentNullException.ThrowIfNull(figures);
        ArgumentNullException.ThrowIfNull(rows);

        RequestedLabel = figures.Requested > 0 ? FreeSpace.Format(figures.Requested) : string.Empty;
        ScheduledLabel = figures.Scheduled > 0 ? FreeSpace.Format(figures.Scheduled) : string.Empty;

        _freeBefore = freeBefore;
        Track(freeNow);

        IsDetailsOpen = opensDetails;

        // Filled where it stands, so the page's binding keeps the collection it was given.
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
        IsDetailsOpen = false;
        Rows.Clear();
        RowsHeading = string.Empty;
        RowsNote = string.Empty;
    }
}
