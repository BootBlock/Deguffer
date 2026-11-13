using Deguffer.Core.Execution.Timing;

namespace Deguffer.Benchmark;

/// <summary>What one run of the plan route recorded beside its own time.</summary>
/// <param name="Times">When each provider took a slot and ended, in the order they ended.</param>
internal sealed record PlanRun(IReadOnlyList<ProviderPlanTime> Times)
{
    /// <summary>The time of the provider <paramref name="id"/>, or null where it did not plan in this run.</summary>
    public ProviderPlanTime? Of(string id)
    {
        foreach (var time in Times)
        {
            if (time.Provider.Id == id)
            {
                return time;
            }
        }

        return null;
    }
}
