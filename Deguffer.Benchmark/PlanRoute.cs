using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Timing;

namespace Deguffer.Benchmark;

/// <summary>
/// A full planning pass of every provider, as a Storage scan runs one, with when each provider took a
/// slot and ended (#443). It only plans: nothing is executed, and nothing a plan offers is removed.
///
/// <para>One planner for every run, as the Storage page keeps one for its life, so the first run is a
/// pass from a fresh process and each later run a warm pass of the same process. Each pass drops the
/// providers' caches at its start, as a scan does, so warm means the system's file cache and this
/// process's compiled code, not a remembered answer.</para>
/// </summary>
internal sealed class PlanRoute(CleanupPlanner planner)
{
    public RouteRun Timed => new(RunAsync);

    private async ValueTask<RunTally> RunAsync(CancellationToken ct)
    {
        var timed = new PlanTimeLog();

        var findings = await planner.PlanAllAsync(timed: timed, ct: ct).ConfigureAwait(false);

        return new RunTally(findings.Count, BytesRead: 0, Complete: true, Planning: new PlanRun(timed.Times));
    }
}
