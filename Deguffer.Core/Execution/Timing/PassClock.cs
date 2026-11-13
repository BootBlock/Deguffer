namespace Deguffer.Core.Execution.Timing;

/// <summary>
/// The time since a planning pass started, on the clock the planner was given, so every
/// <see cref="ProviderPlanTime"/> of one pass is measured from the same instant.
/// </summary>
internal readonly struct PassClock
{
    private readonly TimeProvider _time;
    private readonly long _started;

    public PassClock(TimeProvider time)
    {
        _time = time;
        _started = time.GetTimestamp();
    }

    /// <summary>How long ago the pass started.</summary>
    public TimeSpan Now => _time.GetElapsedTime(_started);
}
