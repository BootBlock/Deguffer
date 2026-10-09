using Deguffer.Core.Execution;
using Deguffer.Core.Providers;

namespace Deguffer.Testing;

/// <summary>
/// Whether what a provider names as its cleaned places covers what its plan goes on to clean
/// (<see cref="ICleanupProvider.CleanedPlacesAsync"/>): every path a step destroys, and every place
/// a tool's own eviction command is sent. A path this finds uncovered is one a duplicate search
/// would let a group keep, and the next clean would delete.
/// </summary>
public static class CleanedPlaceCoverage
{
    /// <summary>Every path <paramref name="plan"/> cleans that no place <paramref name="provider"/> names holds.</summary>
    public static async Task<IReadOnlyList<string>> UncoveredAsync(ICleanupProvider provider, CleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(plan);

        var places = await provider.CleanedPlacesAsync();

        return [.. Cleaned(plan).Where(path => !places.Any(place => place.Holds(path)))];
    }

    /// <summary>
    /// Plans <paramref name="provider"/> and returns what its plan cleans that its places do not
    /// hold, after checking the plan cleans something, so a test whose fixture planned nothing cannot
    /// pass by having nothing to cover.
    /// </summary>
    public static async Task<IReadOnlyList<string>> UncoveredAsync(ICleanupProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var plan = await provider.PlanAsync();

        if (!Cleaned(plan).Any())
        {
            throw new InvalidOperationException($"The plan of '{provider.Id}' cleans nothing, so it proves no coverage.");
        }

        return await UncoveredAsync(provider, plan);
    }

    /// <summary>What a plan cleans: each step's subjects, and where each command is sent.</summary>
    public static IEnumerable<string> Cleaned(CleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Steps.SelectMany(step => step.Subjects)
            .Concat(plan.Steps.OfType<RunCommandStep>().SelectMany(command => command.MeasuredPaths))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
