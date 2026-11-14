namespace Deguffer.Core.Execution;

/// <summary>
/// Where a clean (<see cref="CleanupPlanner.ExecuteAsync"/>) reports as it goes. Each is optional: a
/// caller hears only what it asks for, and a run reported to nobody runs the same.
/// </summary>
public sealed record CleanReporters
{
    /// <summary>Reports to nobody.</summary>
    public static CleanReporters None { get; } = new();

    /// <summary>A sentence naming the plan the run is on, for the status line.</summary>
    public IProgress<string>? Status { get; init; }

    /// <summary>
    /// How far through the whole run, 0 to 1. Unlike planning, execution knows its own extent before
    /// it starts, so this is a fraction rather than a sentence.
    /// </summary>
    public IProgress<double>? Progress { get; init; }

    /// <summary>
    /// Each plan's result the moment it has one, before the next plan starts, so the space a clean
    /// gives back can be counted as it goes rather than only stated at the end. Every result the run
    /// returns is reported here once and in the same order, a plan the clean was cancelled before
    /// starting included, so whatever is summed from these lands on what is summed from the results.
    /// </summary>
    public IProgress<CleanupResult>? Finished { get; init; }

    /// <summary>
    /// The tool the run is waiting on while a command step runs, and null when it returns, so the page
    /// can say what a cancellation pressed now would wait for. See <see cref="CleanCancelNotice"/>.
    /// </summary>
    public IProgress<CommandUnderway?>? Underway { get; init; }

    /// <summary>
    /// Each plan as the run reaches it, before its first step, with the span of <see cref="Progress"/>
    /// it accounts for, so the page can show which row the run is on and how far through that row it
    /// is. A plan the clean was cancelled before starting is reported too, as only checked, because
    /// its §5.6 proof still takes time on that row.
    /// </summary>
    public IProgress<PlanUnderway>? Started { get; init; }
}
