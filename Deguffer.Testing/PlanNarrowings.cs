using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// Plans narrowed to the steps a test chose, as Clean narrows them (<see cref="PlanNarrowing"/>), and a
/// narrowing whose content question a test answers.
/// </summary>
public static class PlanNarrowings
{
    /// <summary><paramref name="plan"/> narrowed to <paramref name="chosen"/>, asking each declined subject's content of the disk.</summary>
    public static async Task<CleanupPlan> NarrowedToAsync(this CleanupPlan plan, IReadOnlyCollection<CleanupStep> chosen) =>
        (await new PlanNarrowing(ScanTuner.Shipped).NarrowAsync([new ChosenSteps(plan, chosen)], CancellationToken.None))[0];

    /// <summary>A narrowing that asks <paramref name="content"/> what each declined subject holds, in place of the disk.</summary>
    public static PlanNarrowing Asking(Func<string, bool> content) => new(ScanTuner.Shipped, content);
}
