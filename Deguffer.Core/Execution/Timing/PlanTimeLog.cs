namespace Deguffer.Core.Execution.Timing;

/// <summary>
/// Every <see cref="ProviderPlanTime"/> one planning pass reported, in the order its rows ended.
///
/// <para>A sink that keeps rather than one that posts: <see cref="Progress{T}"/> would send each time
/// to the thread that made it, and nothing about a time needs the UI thread. Locked, because the
/// contract of <see cref="IProgress{T}"/> does not promise one report at a time, whatever the planner
/// does today.</para>
/// </summary>
public sealed class PlanTimeLog : IProgress<ProviderPlanTime>
{
    private readonly Lock _gate = new();
    private readonly List<ProviderPlanTime> _times = [];

    /// <summary>A copy of what has been reported so far.</summary>
    public IReadOnlyList<ProviderPlanTime> Times
    {
        get
        {
            lock (_gate)
            {
                return [.. _times];
            }
        }
    }

    public void Report(ProviderPlanTime value)
    {
        lock (_gate)
        {
            _times.Add(value);
        }
    }
}
