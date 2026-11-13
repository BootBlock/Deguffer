using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution.Timing;

namespace Deguffer.Benchmark;

/// <summary>What one run of the plan route recorded beside its own time.</summary>
/// <param name="Times">When each provider took a slot and ended, in the order they ended.</param>
/// <param name="Calls">
/// How many of each costly question to Windows the pass asked, and the time they took, indexed by
/// <see cref="MachineCall"/> (#516).
/// </param>
internal sealed record PlanRun(IReadOnlyList<ProviderPlanTime> Times, IReadOnlyList<MachineCallCount> Calls)
{
    /// <summary>Every kind of call <paramref name="tally"/> counted, in the order of <see cref="MachineCall"/>.</summary>
    public static IReadOnlyList<MachineCallCount> CallsOf(MachineCallTally tally) =>
        [.. Enum.GetValues<MachineCall>().Select(tally.Of)];

    /// <summary>What the pass asked of <paramref name="call"/>.</summary>
    public MachineCallCount Of(MachineCall call) => Calls[(int)call];

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
