using Deguffer.Core.Execution;

namespace Deguffer.Benchmark;

/// <summary>
/// What a clean's steps spent their time on, summed over every step: what the clean route reports
/// beside the removals' own passes, so the cost of a step around its removal can be read apart.
/// </summary>
/// <param name="Count">How many steps ran.</param>
/// <param name="Elapsed">Every step, end to end. Less than the run by what the plan costs around its steps.</param>
internal sealed record StepTimes(int Count, TimeSpan Elapsed, TimeSpan UseCheck, TimeSpan Look, TimeSpan Acting)
{
    public static StepTimes Of(IReadOnlyList<StepOutcome> steps) => new(
        steps.Count,
        Sum(steps, step => step.Timing.Elapsed),
        Sum(steps, step => step.Timing.UseCheck),
        Sum(steps, step => step.Timing.Look),
        Sum(steps, step => step.Timing.Acting));

    private static TimeSpan Sum(IReadOnlyList<StepOutcome> steps, Func<StepOutcome, TimeSpan> part) =>
        steps.Aggregate(TimeSpan.Zero, (total, step) => total + part(step));
}
