using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

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
    /// The sources verified by hand in §4.1 and §4.2, plus pip, Cargo, Go, Maven, vcpkg, pnpm,
    /// conda, Playwright, the GPU shader caches, the Chromium application caches, the Firefox
    /// profile caches, the Dart analysis server's byte store, the per-volume Recycle Bins, the crash
    /// dumps, the Windows servicing logs and the per-project build output inside the user's own
    /// approved folders — which the audit did not cover, and which were investigated on their own
    /// terms before being added. Their reasoning and their rejected alternatives are in
    /// <c>docs/cache-locations.md</c>.
    ///
    /// Tier 1 throughout except Unity, Cargo's per-project target, node_modules, Python virtual
    /// environments, conda, Maven, vcpkg, PlatformIO and Playwright, which are Tier 2, and the
    /// Recycle Bins, the crash dumps and the servicing logs, which are Tier 3. Neither tier is ever
    /// pre-selected, and neither is executed without the confirmation §7 requires of it — an
    /// acknowledgement for Tier 2, and for Tier 3 the typed phrase where the user has asked to be
    /// held to it.
    /// </summary>
    public static CleanupPlanner CreateDefault()
    {
        var roots = new SourceRootStore(UserEnvironment.Current);

        // One discovery for every provider that searches the user's own folders, and one live-tree
        // inspector beside it. Shared deliberately rather than defaulted per provider: six unshared
        // passes would each walk the developer's whole disk on an unelevated run, and the names each
        // provider registers on the way in are what make the one pass answer for all of them.
        var sourceTrees = new SourceDirectoryDiscovery(DirectoryScanner.Default);
        var liveTrees = LiveTreeInspector.Default;

        return new CleanupPlanner(
        [
            new DotNetObjProvider(roots, sourceTrees, liveTrees),
            new UnityLibraryProvider(roots, sourceTrees, liveTrees),
            new CargoTargetProvider(roots, sourceTrees, liveTrees),
            new NodeModulesProvider(roots, sourceTrees, liveTrees),
            new PythonVirtualEnvironmentProvider(roots, sourceTrees, liveTrees),
            .. CacheProviders(),
        ]);
    }

    private static IReadOnlyList<ICleanupProvider> CacheProviders() =>
    [
        new NuGetCacheProvider(),
        new GradleCacheProvider(),
        new NpmCacheProvider(),
        new PnpmStoreProvider(),
        new VsCodeCppToolsCacheProvider(),
        new DartAnalysisServerProvider(),
        new UvCacheProvider(),
        new PipCacheProvider(),
        new CondaCacheProvider(),
        new CargoCacheProvider(),
        new GoCacheProvider(),
        new MavenRepositoryProvider(),
        new VcpkgCacheProvider(),
        new GpuShaderCacheProvider(),
        new ChromiumCacheProvider(),
        new FirefoxCacheProvider(),
        new PlatformIoCacheProvider(),
        new PlaywrightBrowsersProvider(),
        new RecycleBinProvider(),
        new CrashDumpProvider(),
        new WindowsServicingLogProvider(),
    ];

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
    /// <param name="keep">
    /// The user's guard on recently touched files. One value for the whole pass, and one instant
    /// inside it: every provider then agrees about which files are recent, and a plan previewed at
    /// the top of the pass protects the same files as one previewed at the bottom.
    /// </param>
    public async Task<IReadOnlyList<Finding>> PlanAllAsync(
        MinimumAge keep = default,
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

            var finding = await PlanOneAsync(provider, keep, status, ct).ConfigureAwait(false);

            findings.Add(finding);
            found?.Report(finding);
        }

        findings.Sort((a, b) => b.EstimatedBytes.CompareTo(a.EstimatedBytes));
        return findings;
    }

    private static async Task<Finding> PlanOneAsync(
        ICleanupProvider provider,
        MinimumAge keep,
        IProgress<string>? status,
        CancellationToken ct)
    {
        status?.Report($"Checking {provider.Name}…");

        var present = await provider.IsPresentAsync(ct).ConfigureAwait(false);
        var awaitingFolders = provider.IsAwaitingSourceFolders;

        // A provider with nowhere approved to look is still asked for its plan, because that plan is
        // where it says which folder to add. Short-circuiting on presence alone left that sentence
        // unreachable: the one provider whose absence the user can do something about was the one
        // that never got to say so.
        if (!present && !awaitingFolders)
        {
            return new Finding(provider, IsPresent: false, Plan: null);
        }

        return new Finding(
            provider,
            present,
            await provider.PlanAsync(keep, ct).ConfigureAwait(false),
            awaitingFolders);
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
    /// <param name="requireTypedPhrase">
    /// The user's preference about §7's typed phrase, which has to reach here as well as the shell:
    /// the requirement is re-derived below, so a shell that stops asking against a planner still
    /// demanding an answer would turn the preference into a refusal to clean. It defaults to the
    /// strict rule, so a caller that says nothing about it fails closed.
    /// </param>
    /// <param name="progress">
    /// How far through the whole run, 0 to 1. Unlike planning, execution knows its own extent
    /// before it starts, so this is a fraction rather than a sentence.
    /// </param>
    public async Task<IReadOnlyList<CleanupResult>> ExecuteAsync(
        IReadOnlyList<Finding> selected,
        IReadOnlyList<Confirmation>? confirmations = null,
        bool requireTypedPhrase = true,
        IProgress<string>? status = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);

        confirmations ??= [];

        // A finding with nothing to remove is not part of the run, so it is dropped before the
        // weights are worked out rather than skipped inside the loop. Otherwise every empty
        // selection would claim a share of the bar and then complete instantly.
        var plans = selected
            .Select(f => (Finding: f, Plan: f.Plan))
            .Where(p => p.Plan is { IsEmpty: false })
            .Select(p => (p.Finding, Plan: p.Plan!))
            .ToList();

        var weights = ProgressWeights.For(plans.Select(p => p.Plan.EstimatedBytes));
        var total = weights.Sum();
        var results = new List<CleanupResult>(plans.Count);
        var done = 0.0;

        for (var i = 0; i < plans.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var (finding, plan) = plans[i];

            // §7's extra confirmation for anything above Tier 1. The requirement is derived here
            // rather than trusted from the caller: a shell that forgot to ask, or asked for the
            // wrong subject, must fail closed rather than delete.
            var requirement = ConfirmationRequirement.For(plan, requireTypedPhrase);

            if (!requirement.IsSatisfiedBy(confirmations))
            {
                throw new ConfirmationRequiredException(requirement);
            }

            status?.Report($"Cleaning {finding.Provider.Name}…");

            results.Add(await finding.Provider
                .ExecuteAsync(plan, ScaledProgress.Within(progress, done / total, weights[i] / total), ct)
                .ConfigureAwait(false));

            // Reported from here rather than trusted from the provider: a provider that reports
            // nothing would otherwise leave the bar wherever the previous one left it, and one that
            // stops short of 1 would leave a gap that never closes.
            done += weights[i];
            progress?.Report(done / total);
        }

        return results;
    }
}
