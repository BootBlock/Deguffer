using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a provider starts at the top of a planning pass (<see cref="ICleanupProvider.BeginPass"/>), held
/// to the contract every adopter shares: the query is started before the provider plans, it is the very
/// answer the plan then awaits, so the pass still asks it once, and a provider that is absent, or whose
/// check before asking refuses, starts nothing.
///
/// <para>The planner calls <see cref="ICleanupProvider.BeginPass"/> before it hands out any slot, which
/// <see cref="CleanupPlannerTests"/> proves by the order it calls each provider, so a query started here
/// has started before the first provider plans.</para>
/// </summary>
internal static class EarlyStartProof
{
    /// <summary>
    /// Begins a pass for <paramref name="provider"/> alone, waits for what it started, and asserts that
    /// each of <paramref name="queries"/> ran exactly once. Then plans, and asserts that planning asked
    /// none of them again.
    /// </summary>
    /// <param name="queries">Text each early query's arguments contain, one per query.</param>
    /// <returns>The plan, for a test that also asserts what it was built from.</returns>
    public static async Task<CleanupPlan> StartsBeforeItPlansAsync(
        ICleanupProvider provider, FakeProcessRunner runner, params string[] queries)
    {
        Assert.NotEmpty(queries);

        provider.InvalidateCaches();
        using var hold = provider.HoldCaches();

        await BeginAsync(provider);

        foreach (var query in queries)
        {
            Assert.True(
                RunsOf(runner, query) == 1,
                $"'{query}' ran {RunsOf(runner, query)} times when {provider.Id}'s pass began, where it should run once before the provider plans.");
        }

        var plan = await provider.PlanAsync();

        foreach (var query in queries)
        {
            Assert.True(
                RunsOf(runner, query) == 1,
                $"'{query}' ran {RunsOf(runner, query)} times by the end of {provider.Id}'s plan: the plan asked again rather than awaiting the answer the pass started.");
        }

        return plan;
    }

    /// <summary>
    /// Begins a pass for <paramref name="provider"/> alone and asserts that it ran no tool: the tool is
    /// not installed, or the check its plan makes before asking refuses.
    /// </summary>
    public static async Task StartsNothingAsync(ICleanupProvider provider, FakeProcessRunner runner)
    {
        provider.InvalidateCaches();
        using var hold = provider.HoldCaches();

        await BeginAsync(provider);

        Assert.Empty(runner.Invocations);
    }

    private static async Task BeginAsync(ICleanupProvider provider)
    {
        using var start = new PassStart(1, CancellationToken.None);

        await provider.BeginPass(start).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static int RunsOf(FakeProcessRunner runner, string query) =>
        runner.Invocations.Count(run => run.Arguments.Contains(query, StringComparison.Ordinal));
}
