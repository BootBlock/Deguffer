using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// How far the clean under way has got: the bar and its figure, and what it has removed so far.
/// Shown while <see cref="CleanViewModel.IsCleaning"/> is up.
/// </summary>
public sealed partial class CleanProgressViewModel : ObservableObject
{
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

    /// <summary>Start again from nothing, for a clean about to run.</summary>
    public void Start()
    {
        _removedSoFar.Clear();
        Percent = 0;
        RemovedSoFarLabel = FreeSpace.Format(RunFigures.For(_removedSoFar).Removed);
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
    }

    /// <summary>Count one more row's run into the figure removed so far.</summary>
    public void Count(CleanupResult result)
    {
        _removedSoFar.Add(result);
        RemovedSoFarLabel = FreeSpace.Format(RunFigures.For(_removedSoFar).Removed);
    }
}
