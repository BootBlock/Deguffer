using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Runs the dry run across every provider, then executes the ones the user chose. §7: preview is
/// the primary action — nothing here touches the disk until <see cref="ExecuteAsync"/>.
///
/// Holds no knowledge of any cache; that lives entirely in the providers.
/// </summary>
public sealed class CleanupPlanner
{
    private readonly IReadOnlyList<ICleanupProvider> _providers;

    public CleanupPlanner(IEnumerable<ICleanupProvider> providers) => _providers = [.. providers];

    /// <summary>The Milestone 1 set: the three Tier 1 sources verified by hand in §4.1.</summary>
    public static CleanupPlanner CreateDefault() => new(
    [
        new NuGetCacheProvider(),
        new GradleCacheProvider(),
        new NpmCacheProvider(),
    ]);

    public IReadOnlyList<ICleanupProvider> Providers => _providers;

    /// <summary>
    /// Preview every provider, largest first (§7: group by cause, sort by size).
    ///
    /// Deliberately sequential. Each provider fans out internally to measure its tree, so running
    /// providers concurrently as well would multiply into dozens of simultaneous enumerations
    /// against one disk — slower, not faster, for the same reason execution is sequential.
    /// </summary>
    public async Task<IReadOnlyList<Finding>> PlanAllAsync(
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        // Every provider drops its cached view of the machine before any of them plans. Doing
        // this up front rather than per-provider matters: the providers share collaborators by
        // default, so invalidating inside the loop would throw away the snapshot the previous
        // provider just paid for.
        foreach (var provider in _providers)
        {
            provider.InvalidateCaches();
        }

        var findings = new List<Finding>(_providers.Count);

        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            findings.Add(await PlanOneAsync(provider, status, ct).ConfigureAwait(false));
        }

        findings.Sort((a, b) => b.EstimatedBytes.CompareTo(a.EstimatedBytes));
        return findings;
    }

    private static async Task<Finding> PlanOneAsync(
        ICleanupProvider provider,
        IProgress<string>? status,
        CancellationToken ct)
    {
        status?.Report($"Checking {provider.Name}…");

        if (!await provider.IsPresentAsync(ct).ConfigureAwait(false))
        {
            return new Finding(provider, IsPresent: false, Plan: null);
        }

        return new Finding(provider, IsPresent: true, await provider.PlanAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Execute the given plans in sequence. Sequential is deliberate: two package managers
    /// hammering the same disk at once is slower, not faster, and progress stays meaningful.
    /// </summary>
    public async Task<IReadOnlyList<CleanupResult>> ExecuteAsync(
        IReadOnlyList<Finding> selected,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);

        var results = new List<CleanupResult>(selected.Count);

        foreach (var finding in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (finding.Plan is not { IsEmpty: false } plan)
            {
                continue;
            }

            // Milestone 1 ships Tier 1 only. This guard exists so that the first Tier 2/3
            // provider cannot silently inherit a path that deletes without the extra
            // confirmation §7 requires — it has to come here and decide deliberately.
            if (plan.Tier != SafetyTier.RegenerableCache)
            {
                throw new NotSupportedException(
                    $"'{plan.ProviderName}' is {plan.Tier.ToDisplayName()}. Only Tier 1 is executable " +
                    "until the confirmation flow required by §7 exists.");
            }

            status?.Report($"Cleaning {finding.Provider.Name}…");
            results.Add(await finding.Provider.ExecuteAsync(plan, progress: null, ct).ConfigureAwait(false));
        }

        return results;
    }
}
