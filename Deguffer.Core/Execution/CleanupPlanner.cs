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
    /// The sources verified by hand in §4.1 and §4.2, plus pip, Poetry, Cargo, Go, Maven, vcpkg, pnpm,
    /// conda, Playwright, the GPU shader caches, the Chromium application caches, the Firefox
    /// profile caches, the Epic Games launcher's store cache and its own logs, the Steam client's
    /// web caches, the Spotify desktop app's streaming cache, the Squirrel updater's staging and the builds it superseded, the Dart analysis
    /// server's byte store, Roslyn's solution indexes, the Azure Functions Core Tools releases Visual Studio downloads, what
    /// Claude Code's sessions leave behind, its rewind snapshots and the logs of the MCP servers it runs, the
    /// per-volume Recycle Bins, the Windows File History target, the crash
    /// dumps, the Windows servicing logs and the per-project build output inside the user's own
    /// approved folders — which the audit did not cover, and which were investigated on their own
    /// terms before being added. Their reasoning and their rejected alternatives are in
    /// <c>docs/cache-locations.md</c>.
    ///
    /// Tier 1 throughout except Unity, Cargo's per-project target, node_modules, Python virtual
    /// environments, conda, Maven, vcpkg, PlatformIO, Playwright, the Azure Functions Core Tools
    /// releases and the superseded Squirrel builds, which are Tier 2, and the
    /// Recycle Bins, the File History target, the crash dumps, the servicing logs, the Epic
    /// launcher's logs, the VS Code logs, and Claude Code's rewind snapshots and MCP server logs, which are Tier 3. Neither tier is ever
    /// pre-selected, and neither is executed without the confirmation §7 requires of it — an
    /// acknowledgement for Tier 2, and for Tier 3 the typed phrase where the user has asked to be
    /// held to it.
    /// </summary>
    /// <param name="preferences">
    /// The live settings, for the two providers the user parameterises: the route a Recycle Bin is
    /// emptied by, and the age past which a File History version may go. Defaulted to the
    /// shipped values so a caller outside the app — a test, or the Explore page's own provider list
    /// — behaves as an untouched install would. It is passed rather than captured because the
    /// provider reads it at plan time, which is what makes a change on the Settings page take
    /// effect from the next preview.
    /// </param>
    public static CleanupPlanner CreateDefault(ICurrentPreferences? preferences = null)
    {
        var roots = new SourceRootStore(UserEnvironment.Current);

        // One discovery for every provider that searches the user's own folders, and one live-tree
        // inspector beside it. Shared deliberately rather than defaulted per provider: six unshared
        // passes would each walk the developer's whole disk on an unelevated run, and the names each
        // provider registers on the way in are what make the one pass answer for all of them.
        var sourceTrees = new SourceDirectoryDiscovery(DirectoryScanner.Default);
        var liveTrees = LiveTreeInspector.Default;

        // One sweep of %LOCALAPPDATA% for both Squirrel providers, on the reasoning above: they ask
        // the same question of the same directory, and two unshared discoveries would list a folder
        // holding hundreds of children twice per pass.
        var squirrel = new SquirrelDiscovery(UserEnvironment.Current);

        // One reading of Claude Code's list of running sessions for both providers over its folder, on the
        // same reasoning: every entry in it costs a process probe, and two unshared registries would ask
        // about each running process twice per pass.
        var claudeSessions = new ClaudeCodeSessionRegistry(UserEnvironment.Current, ProcessInspector.Default);

        return new CleanupPlanner(
        [
            new DotNetObjProvider(roots, sourceTrees, liveTrees),
            new UnityLibraryProvider(roots, sourceTrees, liveTrees),
            new CargoTargetProvider(roots, sourceTrees, liveTrees),
            new NodeModulesProvider(roots, sourceTrees, liveTrees),
            new PythonVirtualEnvironmentProvider(roots, sourceTrees, liveTrees),
            .. CacheProviders(squirrel, claudeSessions, liveTrees, preferences ?? DefaultPreferences.Instance),
        ]);
    }

    private static IReadOnlyList<ICleanupProvider> CacheProviders(
        SquirrelDiscovery squirrel,
        ClaudeCodeSessionRegistry claudeSessions,
        ILiveTreeInspector liveTrees,
        ICurrentPreferences preferences) =>
    [
        new NuGetCacheProvider(),
        new GradleCacheProvider(),
        new NpmCacheProvider(),
        new PnpmStoreProvider(),
        new VsCodeCppToolsCacheProvider(),
        new DartAnalysisServerProvider(),
        new RoslynCacheProvider(),
        new UvCacheProvider(),
        new PipCacheProvider(),
        new PoetryCacheProvider(),
        new CondaCacheProvider(),
        new CargoCacheProvider(),
        new GoCacheProvider(),
        new MavenRepositoryProvider(),
        new VcpkgCacheProvider(),
        new GpuShaderCacheProvider(),
        new ChromiumCacheProvider(),
        new VsCodeCacheProvider(),
        new FirefoxCacheProvider(),
        new EpicLauncherWebCacheProvider(),
        new EpicLauncherContentCacheProvider(),
        new SteamCacheProvider(),
        new SpotifyCacheProvider(),
        new SquirrelStagingProvider(discovery: squirrel, liveTrees: liveTrees),
        new PlatformIoCacheProvider(),
        new PlaywrightBrowsersProvider(),
        new SquirrelSupersededVersionProvider(discovery: squirrel, liveTrees: liveTrees),
        new AzureFunctionsToolsProvider(),
        new ClaudeCodeDerivedStateProvider(sessions: claudeSessions),
        new RecycleBinProvider(preferences: preferences),
        new FileHistoryProvider(preferences: preferences),
        new TempDirectoryProvider(liveTrees: liveTrees, preferences: preferences),
        new CrashDumpProvider(),
        new WindowsServicingLogProvider(),
        new EpicLauncherLogProvider(),
        new VsCodeLogProvider(),
        new ClaudeCodeMcpLogProvider(),
        new ClaudeCodeFileHistoryProvider(sessions: claudeSessions),
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
    ///
    /// <para><b>A plan with no steps and something to prove is run too, last, and only to be
    /// verified.</b> Every candidate it found was withheld, so what it holds is a promise that those
    /// paths will be standing afterwards (§5.6) — and that promise is about what the whole run
    /// leaves, so it is checked once every deletion has happened. It destroys nothing, so §7 asks
    /// nothing of it and the bar gives it no share. See <see cref="CleanupPlan.HasSomethingToProve"/>.
    /// </para>
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

        // A finding with nothing to remove and nothing to prove is not part of the run, so it is
        // dropped here rather than skipped inside the loop. One with nothing to remove and a
        // survivor to prove stays, because verifying that survivor is the whole of what it is for.
        //
        // Those go last, whatever order the selection arrived in: their promise is about what the
        // whole run leaves standing, and checking it before a later plan has deleted anything would
        // prove nothing about that plan. OrderBy is stable, so the plans that delete keep the order
        // they were given.
        var plans = selected
            .Select(f => (Finding: f, Plan: f.Plan))
            .Where(p => p.Plan is { IsEmpty: false } or { HasSomethingToProve: true })
            .Select(p => (p.Finding, Plan: p.Plan!))
            .OrderBy(p => p.Plan.IsEmpty)
            .ToList();

        // §5.6's negative is answered against what the whole run may destroy, not against each
        // plan's own targets. Gathered once, before the first deletion, because a provider that
        // verifies halfway through has to be able to tell a folder a *later* provider will take
        // from one a stranger already took — and because the set does not change while the run
        // proceeds.
        var reach = RunReach.Of([.. plans.Select(p => p.Plan)]);

        // What the run's removals leave standing, written by each plan as it executes and read by
        // every verification after it. One record for the whole run, for the reason the reach is one
        // value: a folder one provider's removal went into may be a folder another provider promised
        // to leave. See RunResidue.
        var residue = new RunResidue();

        var weights = Weigh([.. plans.Select(p => p.Plan)]);
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
            //
            // A plan with no steps is not asked about at any tier. It destroys nothing, so there is
            // nothing to authorise, and failing closed on it would throw the rest of the run away
            // over a check that only reads the disk.
            if (!plan.IsEmpty)
            {
                var requirement = ConfirmationRequirement.For(plan, requireTypedPhrase);

                if (!requirement.IsSatisfiedBy(confirmations))
                {
                    throw new ConfirmationRequiredException(requirement);
                }
            }

            status?.Report(plan.IsEmpty
                ? $"Checking what {finding.Provider.Name} left alone…"
                : $"Cleaning {finding.Provider.Name}…");

            results.Add(await finding.Provider
                .ExecuteAsync(
                    plan,
                    reach,
                    residue,
                    ScaledProgress.Within(progress, done / total, weights[i] / total),
                    ct)
                .ConfigureAwait(false));

            // Reported from here rather than trusted from the provider: a provider that reports
            // nothing would otherwise leave the bar wherever the previous one left it, and one that
            // stops short of 1 would leave a gap that never closes.
            done += weights[i];
            progress?.Report(done / total);
        }

        return results;
    }

    /// <summary>
    /// Each plan's share of the bar: a plan that deletes is weighted by what it frees, and a plan run
    /// only to be verified gets nothing, because the part of a run that takes any time is the
    /// deleting.
    ///
    /// <para>Weighed apart rather than handed to <see cref="ProgressWeights"/> as a zero. That rule
    /// shares the bar equally where nothing carries a figure, so beside a command step whose tool
    /// reports none the verification would take half, and the bar would sit there while the command
    /// ran. Only a run holding nothing but verification shares the bar between those plans.</para>
    /// </summary>
    private static IReadOnlyList<double> Weigh(IReadOnlyList<CleanupPlan> plans)
    {
        var deleting = ProgressWeights.For(plans.Where(p => !p.IsEmpty).Select(p => p.EstimatedBytes));

        if (deleting.Count == 0)
        {
            return ProgressWeights.For(plans.Select(_ => 0L));
        }

        var weights = new List<double>(plans.Count);
        var next = 0;

        foreach (var plan in plans)
        {
            weights.Add(plan.IsEmpty ? 0 : deleting[next++]);
        }

        return weights;
    }
}
