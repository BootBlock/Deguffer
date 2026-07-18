using Deguffer.Core.Providers;

namespace Deguffer.Core.Execution;

/// <summary>One provider's contribution to the preview, present or not.</summary>
/// <param name="Provider">The provider that produced it.</param>
/// <param name="IsPresent">Whether the toolchain exists on this machine at all.</param>
/// <param name="Plan">The dry run. Null only when the toolchain is absent.</param>
public sealed record Finding(ICleanupProvider Provider, bool IsPresent, CleanupPlan? Plan)
{
    public long EstimatedBytes => Plan?.EstimatedBytes ?? 0;

    /// <summary>Whether there is anything here worth showing the user as reclaimable.</summary>
    public bool HasReclaimableSpace => EstimatedBytes > 0;
}

/// <summary>
/// Runs the dry run across every provider. §7: this is the primary action — deleting is a
/// separate, second step, and nothing here touches the disk.
/// </summary>
public sealed class CleanupPlanner
{
    private readonly IReadOnlyList<ICleanupProvider> _providers;

    public CleanupPlanner(IEnumerable<ICleanupProvider> providers)
    {
        _providers = [.. providers];
    }

    /// <summary>The Milestone 1 set: the three Tier 1 sources verified by hand in §4.1.</summary>
    public static CleanupPlanner CreateDefault() => new(
    [
        new NuGetCacheProvider(),
        new GradleCacheProvider(),
        new NpmCacheProvider(),
    ]);

    public IReadOnlyList<ICleanupProvider> Providers => _providers;

    /// <summary>
    /// Preview every provider, largest first (§7: group by cause, sort by size). Providers run
    /// concurrently because each is dominated by directory enumeration.
    /// </summary>
    public async Task<IReadOnlyList<Finding>> PlanAllAsync(
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var findings = await Task.WhenAll(_providers.Select(p => PlanOneAsync(p, status, ct))).ConfigureAwait(false);

        return [.. findings.OrderByDescending(f => f.EstimatedBytes)];
    }

    private static async Task<Finding> PlanOneAsync(
        ICleanupProvider provider,
        IProgress<string>? status,
        CancellationToken ct)
    {
        status?.Report($"Checking {provider.Name}…");

        var present = await provider.IsPresentAsync(ct).ConfigureAwait(false);
        if (!present)
        {
            return new Finding(provider, IsPresent: false, Plan: null);
        }

        var plan = await provider.PlanAsync(ct).ConfigureAwait(false);
        return new Finding(provider, IsPresent: true, plan);
    }

    /// <summary>
    /// Execute the given plans in sequence. Sequential is deliberate: two package managers
    /// hammering the same disk at once is slower, not faster, and the progress reporting stays
    /// meaningful.
    /// </summary>
    public async Task<IReadOnlyList<CleanupResult>> ExecuteAsync(
        IReadOnlyList<Finding> selected,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var results = new List<CleanupResult>();

        foreach (var finding in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (finding.Plan is not { IsEmpty: false } plan)
            {
                continue;
            }

            status?.Report($"Cleaning {finding.Provider.Name}…");
            results.Add(await finding.Provider.ExecuteAsync(plan, progress: null, ct).ConfigureAwait(false));
        }

        return results;
    }
}
