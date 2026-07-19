using Deguffer.Core.Configuration;
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

    /// <summary>
    /// The sources verified by hand in §4.1 and §4.2, plus pip and Playwright — which the audit did
    /// not cover, and which were investigated on their own terms before being added. Their reasoning
    /// and their rejected alternatives are in <c>docs/cache-locations.md</c>.
    ///
    /// Tier 1 throughout except PlatformIO and Playwright, which are Tier 2 and therefore offered but
    /// never pre-selected, and never executed without §7's confirmation.
    /// </summary>
    public static CleanupPlanner CreateDefault() => new(
    [
        new DotNetObjProvider(new SourceRootStore(UserEnvironment.Current)),
        new NuGetCacheProvider(),
        new GradleCacheProvider(),
        new NpmCacheProvider(),
        new VsCodeCppToolsCacheProvider(),
        new UvCacheProvider(),
        new PipCacheProvider(),
        new PlatformIoCacheProvider(),
        new PlaywrightBrowsersProvider(),
    ]);

    public IReadOnlyList<ICleanupProvider> Providers => _providers;

    /// <summary>
    /// Preview every provider, largest first (§7: group by cause, sort by size).
    ///
    /// Deliberately sequential. Each provider fans out internally to measure its tree, so running
    /// providers concurrently as well would multiply into dozens of simultaneous enumerations
    /// against one disk — slower, not faster, for the same reason execution is sequential.
    ///
    /// <paramref name="found"/> receives each finding the moment it is ready, so the preview can
    /// fill in as it goes rather than staying blank until the slowest provider finishes (§5.5:
    /// never block on a complete scan). The returned list is the same findings, sorted.
    /// </summary>
    public async Task<IReadOnlyList<Finding>> PlanAllAsync(
        IProgress<string>? status = null,
        IProgress<Finding>? found = null,
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

            var finding = await PlanOneAsync(provider, status, ct).ConfigureAwait(false);

            findings.Add(finding);
            found?.Report(finding);
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
    /// <param name="confirmations">
    /// The answers §7 requires for anything above Tier 1, collected before execution begins because
    /// §7 makes deleting the deliberate second step. A plan whose requirement is unmet throws rather
    /// than being skipped: silently dropping it would report success for work not done.
    /// </param>
    public async Task<IReadOnlyList<CleanupResult>> ExecuteAsync(
        IReadOnlyList<Finding> selected,
        IReadOnlyList<Confirmation>? confirmations = null,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);

        confirmations ??= [];

        var results = new List<CleanupResult>(selected.Count);

        foreach (var finding in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (finding.Plan is not { IsEmpty: false } plan)
            {
                continue;
            }

            // §7's extra confirmation for anything above Tier 1. The requirement is derived here
            // rather than trusted from the caller: a shell that forgot to ask, or asked for the
            // wrong subject, must fail closed rather than delete.
            var requirement = ConfirmationRequirement.For(plan);

            if (!requirement.IsSatisfiedBy(confirmations))
            {
                throw new ConfirmationRequiredException(requirement);
            }

            status?.Report($"Cleaning {finding.Provider.Name}…");
            results.Add(await finding.Provider.ExecuteAsync(plan, progress: null, ct).ConfigureAwait(false));
        }

        return results;
    }
}
