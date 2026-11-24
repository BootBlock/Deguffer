using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>What a <see cref="StepRoute"/> carries out one step with. See <see cref="PlanExecutor"/>.</summary>
/// <param name="Run">What every step of the plan shares.</param>
/// <param name="Clock">What the step's use check and any look are timed on.</param>
/// <param name="Progress">Where the step reports how far through it is, as a fraction.</param>
internal sealed record StepRun(PlanRun Run, StepClock Clock, IProgress<double>? Progress)
{
    /// <summary>What the plan asked to leave alone because it changed too recently.</summary>
    public MinimumAge Keep => Run.Plan.Keep;
}
