using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The card that states what the last clean did: its figures, and its §5.6 safety check with the
/// report a GitHub issue can quote. It stands from the moment the run ends until the next scan or
/// clean replaces it, so it outlives the re-plan that follows the run.
/// </summary>
/// <param name="origin">Which build ran the clean, and with what rights, which the report names.</param>
/// <param name="environment">The profile the report writes its paths against.</param>
public sealed partial class CleanRunResultViewModel(ReportOrigin origin, IUserEnvironment environment) : ObservableObject
{
    /// <summary>
    /// §5.4: two different numbers, reported separately. What Deguffer measured itself removing,
    /// and how the volume's free space actually changed — they disagree whenever anything else on
    /// the machine writes during the run, and presenting them as one number invites distrust.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunResult))]
    public partial string RemovedLabel { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FreeSpaceChangeLabel { get; private set; } = string.Empty;

    /// <summary>
    /// A third number, for a run that had one: what sync apps were asked to release. Apart from both
    /// above, because it is neither something Deguffer removed nor a change anybody measured. The
    /// release happens after the run, so the free space change may not show it yet. See
    /// <see cref="StepOutcome.BytesRequested"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequested))]
    public partial string RequestedLabel { get; private set; } = string.Empty;

    public bool HasRequested => !string.IsNullOrEmpty(RequestedLabel);

    /// <summary>
    /// A fourth number, for a run that had one: what a program marked to remove the next time it
    /// starts. Apart from the others, because nothing has gone yet and the free space change cannot
    /// show it until the user opens that program. See <see cref="StepOutcome.BytesScheduled"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScheduled))]
    public partial string ScheduledLabel { get; private set; } = string.Empty;

    public bool HasScheduled => !string.IsNullOrEmpty(ScheduledLabel);

    /// <summary>The run's own §5.6 verdict, beside its figures, for as long as those figures stand.</summary>
    public SafetyCheckViewModel SafetyCheck { get; } = new();

    public bool HasRunResult => !string.IsNullOrEmpty(RemovedLabel);

    /// <summary>
    /// §5.6 is reported, not just performed, so the run's verdict goes somewhere that outlives the
    /// run: this card, beside the figures it already carries.
    /// </summary>
    /// <param name="freeBefore">The profile volume's free space before the run, where it could be read.</param>
    /// <param name="freeAfter">The same, read once the run has ended.</param>
    public RunOutcome Record(IReadOnlyList<CleanupResult> results, long? freeBefore, long? freeAfter)
    {
        var figures = RunFigures.For(results);

        // FreeSpace states the entries only where no bytes went.
        RequestedLabel = figures.Requested > 0 ? FreeSpace.Format(figures.Requested) : string.Empty;
        ScheduledLabel = figures.Scheduled > 0 ? FreeSpace.Format(figures.Scheduled) : string.Empty;

        FreeSpaceChangeLabel = RunFigures.FreeSpaceChange(freeBefore, freeAfter) is { } change
            ? FreeSpace.Format(change)
            : "—";

        var outcome = RunOutcome.For(results);
        SafetyCheck.Show(outcome, figures.Checks, CleanRunReport.Describe(results, origin, environment));

        // Last, because it is what raises HasRunResult, and the page brings the card in on that: every
        // figure and the verdict are in place by then.
        RemovedLabel = FreeSpace.Format(figures.Removed);

        return outcome;
    }

    /// <summary>Take the card down: its figures describe a machine state the next run replaces.</summary>
    public void Clear()
    {
        RemovedLabel = string.Empty;
        FreeSpaceChangeLabel = string.Empty;
        RequestedLabel = string.Empty;
        ScheduledLabel = string.Empty;
        SafetyCheck.Clear();
    }
}
