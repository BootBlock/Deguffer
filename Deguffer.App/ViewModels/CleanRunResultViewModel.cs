using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The card that states what the last clean did: its figures, its §5.6 verdict and the report a
/// GitHub issue can quote. It stands from the moment the run ends until the next scan or clean
/// replaces it, so it outlives the re-plan that follows the run.
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

    /// <summary>
    /// The run's own §5.6 verdict, beside its figures, for as long as those figures stand.
    ///
    /// <para>The info bar cannot be its home. The bar is a single line describing whatever happened
    /// most recently, and what happens immediately after a clean is the re-plan that follows it —
    /// so a verdict left there is displaced by the next provider's progress line. §5.6 exists to
    /// turn "I think it worked" into evidence the user sees, which a message visible for the length
    /// of one rescan is not.</para>
    /// </summary>
    [ObservableProperty]
    public partial string RunStatement { get; private set; } = string.Empty;

    /// <summary>
    /// Whether that verdict is a failure, so the card can say so in more than words. The words
    /// carry it on their own (§6.5) — the colour is what stops a protected path having gone missing
    /// reading like the two neutral figures beside it.
    /// </summary>
    [ObservableProperty]
    public partial bool RunVerificationFailed { get; private set; }

    /// <summary>
    /// Every §5.6 check the run has something to answer for, in full and unabridged.
    ///
    /// <para>The sentence above tells the user a protected path went missing and, when it is a
    /// failure, asks them to report it. Without this it asks them to report something it never told
    /// them: the paths, the reasons they were protected, and what was found instead all existed in
    /// <see cref="VerificationResult"/> and reached no surface at all. A request to report a fault,
    /// with no way to say what the fault was, is a dead end.</para>
    ///
    /// <para>Every kind of check that is not a pass appears here, because each describes a path
    /// nobody could verify. The sentence is what separates a fault to report from a folder that went
    /// away on its own or one Windows would not describe, and each row's own detail repeats which of
    /// those it is.</para>
    /// </summary>
    public ObservableCollection<VerificationCheck> RunVerificationNotes { get; } = [];

    /// <summary>Whether there is anything in that list, so the card shows it only when there is.</summary>
    public bool HasRunVerificationNotes => RunVerificationNotes.Count > 0;

    public bool HasRunResult => !string.IsNullOrEmpty(RemovedLabel);

    /// <summary>
    /// The last run written out for a GitHub issue, which the card's link copies. Empty where no
    /// run's figures are on screen. See <see cref="CleanRunReport"/>.
    /// </summary>
    public string RunDiagnostics { get; private set; } = string.Empty;

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
        RemovedLabel = FreeSpace.Format(figures.Removed);
        RequestedLabel = figures.Requested > 0 ? FreeSpace.Format(figures.Requested) : string.Empty;
        ScheduledLabel = figures.Scheduled > 0 ? FreeSpace.Format(figures.Scheduled) : string.Empty;

        FreeSpaceChangeLabel = RunFigures.FreeSpaceChange(freeBefore, freeAfter) is { } change
            ? FreeSpace.Format(change)
            : "—";

        var outcome = RunOutcome.For(results);
        RunStatement = outcome.Statement;
        RunDiagnostics = CleanRunReport.Describe(results, origin, environment);
        RunVerificationFailed = outcome.VerificationFailed;

        // Written over rather than emptied and filled, so a second run that meets the same checks
        // leaves the panel alone instead of taking it down and putting it back up.
        LiveList.Rewrite(RunVerificationNotes, figures.Checks);

        OnPropertyChanged(nameof(HasRunVerificationNotes));
        OnPropertyChanged(nameof(HasRunResult));

        return outcome;
    }

    /// <summary>Take the card down: its figures describe a machine state the next run replaces.</summary>
    public void Clear()
    {
        RemovedLabel = string.Empty;
        FreeSpaceChangeLabel = string.Empty;
        RequestedLabel = string.Empty;
        ScheduledLabel = string.Empty;
        RunStatement = string.Empty;
        RunDiagnostics = string.Empty;
        RunVerificationFailed = false;
        RunVerificationNotes.Clear();
        OnPropertyChanged(nameof(HasRunVerificationNotes));
        OnPropertyChanged(nameof(HasRunResult));
    }
}
