using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// How far the clean under way has got: the bar and its figure, what it has removed so far, and the
/// row it is on.
/// Shown while <see cref="CleanViewModel.IsCleaning"/> is up.
/// </summary>
public sealed partial class CleanProgressViewModel : ObservableObject
{
    /// <summary>A hundredth of a row: a few pixels across the widest window the list fits in.</summary>
    private const double RowStep = 0.01;

    private readonly List<CleanupResult> _removedSoFar = [];

    /// <summary>How far through the clean, 0 to 100.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentLabel))]
    public partial double Percent { get; private set; }

    /// <summary>
    /// The same figure in words, beside the bar. §6.5's rule for the backdrop is the same rule
    /// here: nothing may be readable only as a graphic.
    /// </summary>
    public string PercentLabel => $"{Percent:0}%";

    /// <summary>
    /// What the clean under way has removed so far, as each row's run finishes. Counted from the
    /// run's own results the way <see cref="CleanViewModel.RemovedLabel"/> is (<see cref="RunFigures"/>),
    /// so it never runs ahead of what was removed and it lands on the figure the card states when the
    /// run ends.
    /// </summary>
    [ObservableProperty]
    public partial string RemovedSoFarLabel { get; private set; } = string.Empty;

    /// <summary>
    /// The row the run is on and what it is doing there, or null between runs and for a plan no row is
    /// listed for. Raised after <see cref="RowFraction"/>, so a listener reading it as this changes
    /// reads the new row's.
    /// </summary>
    [ObservableProperty]
    public partial RowUnderway? Underway { get; private set; }

    /// <summary>How far through the row <see cref="Underway"/> names the run is, 0 to 1.</summary>
    [ObservableProperty]
    public partial double RowFraction { get; private set; }

    /// <summary>The plan the run is on, which turns the run's progress into the row's.</summary>
    private PlanUnderway? _plan;

    /// <summary>
    /// Start again from nothing, for a clean about to run over <paramref name="rows"/>. Returns what the
    /// run reports each plan it reaches to, which finds the plan's row and is heard on the thread this
    /// is called on. Matched by provider rather than by finding: the run is handed narrowed findings,
    /// and the proof of a registered disk is a finding the planner makes for itself.
    /// </summary>
    public IProgress<PlanUnderway> Start(IReadOnlyList<FindingViewModel> rows)
    {
        _removedSoFar.Clear();
        Percent = 0;
        RemovedSoFarLabel = FreeSpace.Format(RunFigures.For(_removedSoFar).Removed);
        Finish();

        return new Progress<PlanUnderway>(plan => Begin(plan, rows.FirstOrDefault(row => row.Finding.Provider == plan.Provider)));
    }

    /// <summary>The run has reached <paramref name="plan"/>, which is <paramref name="row"/>'s.</summary>
    private void Begin(PlanUnderway plan, FindingViewModel? row)
    {
        _plan = plan;
        RowFraction = plan.Within(plan.From);
        Underway = row is null ? null : new RowUnderway(row, plan.Stage);
    }

    /// <summary>The run is over, so no row is being worked on.</summary>
    public void Finish()
    {
        _plan = null;
        Underway = null;
        RowFraction = 0;
    }

    /// <summary>
    /// Takes a report from the run and moves the bar, but not for every one of them.
    ///
    /// A directory removal reports every 256 files, which on a fast volume is hundreds of reports a
    /// second, and each one that reaches the property raises a change notification and re-lays out
    /// the bar. Half a percent is under two pixels on any window this screen fits in, so anything
    /// smaller is spent for nothing. The end is always taken, because the one value the bar has to
    /// arrive at is the last one.
    /// </summary>
    public void Advance(double fraction)
    {
        var percent = Math.Clamp(fraction * 100, 0, 100);

        if (percent >= 100 || percent - Percent >= 0.5)
        {
            Percent = percent;
        }

        // Thinned on the row's own scale rather than the run's: one row of many crosses its whole
        // width in less than the bar's half a percent, and would otherwise leap from empty to full.
        if (_plan?.Within(fraction) is { } within && (within >= 1 || within - RowFraction >= RowStep))
        {
            RowFraction = within;
        }
    }

    /// <summary>Count one more row's run into the figure removed so far.</summary>
    public void Count(CleanupResult result)
    {
        _removedSoFar.Add(result);
        RemovedSoFarLabel = FreeSpace.Format(RunFigures.For(_removedSoFar).Removed);
    }
}
