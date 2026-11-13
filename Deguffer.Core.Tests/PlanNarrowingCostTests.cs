using System.Collections;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="ChosenSteps"/> is built on the UI thread when Clean is pressed, once per row, and
/// a row of thousands of steps is ordinary. The choice is asked of a set, so the cost grows with the
/// steps rather than with the steps times the ticked steps, and the set compares steps by value, so
/// the steps kept and the steps declined are the ones they were before.
/// </summary>
public sealed class PlanNarrowingCostTests
{
    private const string Root = @"C:\Users\testuser\AppData\Local\example";

    /// <summary>
    /// A collection that is only enumerable makes a linear search walk it once for every step, so the
    /// count of walks is the count of searches. A timing would say the same and be flaky on a shared
    /// machine.
    /// </summary>
    [Fact]
    public async Task TheChoiceIsWalkedOnceHoweverManyStepsThePlanHas()
    {
        var plan = PlanOf(1_000);
        var chosen = new CountedChoice([.. plan.Steps.Take(999)]);

        var narrowed = await plan.NarrowedToAsync(chosen);

        Assert.Equal(1, chosen.Walks);
        Assert.Equal(999, narrowed.Steps.Count);
    }

    /// <summary>
    /// The shell's steps are the plan's own instances, but a step is a value, so an equal step that is
    /// another instance is the same choice. Each declined step stays standing under §5.6.
    /// </summary>
    [Fact]
    public async Task AStepIsChosenByValueAndEveryDeclinedStepIsProtected()
    {
        var plan = PlanOf(6);
        IReadOnlyList<CleanupStep> chosen = [Step(1), Step(4)];

        var narrowed = await plan.NarrowedToAsync(chosen);

        Assert.Equal([PathOf(1), PathOf(4)], narrowed.TargetedPaths);
        Assert.Equal(
            [PathOf(0), PathOf(2), PathOf(3), PathOf(5)],
            narrowed.ProtectedPaths.Select(protection => protection.Path));
        Assert.All(narrowed.ProtectedPaths, protection => Assert.Equal(PathPresence.Present, protection.PresenceBefore));
    }

    [Fact]
    public async Task ChoosingEveryStepByValueLeavesThePlanAsItWas()
    {
        var plan = PlanOf(3);

        Assert.Same(plan, await plan.NarrowedToAsync([Step(0), Step(1), Step(2)]));
    }

    private static CleanupPlan PlanOf(int steps) => new()
    {
        ProviderId = "example",
        ProviderName = "Example",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = [.. Enumerable.Range(0, steps).Select(Step)],
    };

    private static DeleteDirectoryStep Step(int index) => new(PathOf(index), $"Item {index}");

    private static string PathOf(int index) => $@"{Root}\item-{index}";

    private sealed class CountedChoice(IReadOnlyList<CleanupStep> steps) : IReadOnlyCollection<CleanupStep>
    {
        public int Walks { get; private set; }

        public int Count => steps.Count;

        public IEnumerator<CleanupStep> GetEnumerator()
        {
            Walks++;
            return steps.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
