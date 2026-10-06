using Deguffer.Core.Cloud;
using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.SystemProtection;

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
    private readonly RowDeclarations? _declarations;

    /// <param name="declarations">
    /// The declarations the rows that walk a folder consult, admitted here so that they name every row
    /// in this planner and no other, and dropped at the start of every planning pass alongside the
    /// rows' own caches. See <see cref="RowDeclarations"/>.
    /// </param>
    public CleanupPlanner(IEnumerable<ICleanupProvider> providers, RowDeclarations? declarations = null)
    {
        _providers = [.. providers];
        _declarations = declarations;
        _declarations?.Admit(_providers);
    }

    /// <summary>
    /// The sources verified by hand in §4.1 and §4.2, plus pip, Poetry, Cargo, Go, Zig, Maven, vcpkg, pnpm,
    /// conda, Playwright, Puppeteer, the browser profiles test runners leave in the temporary folders, the GPU
    /// shader caches, the Chromium application caches and the offline storage sites keep beside them, the Firefox
    /// profile caches, the Epic Games launcher's store cache and its own logs, the Battle.net
    /// launcher's cache and its logs, the Steam client's
    /// HTTP cache and the shader caches it downloads per game, the Unreal Engine derived data cache every project shares, the Spotify desktop app's streaming cache, the transcoder leftovers of Plex, Jellyfin and Emby, Affinity's machine-learning models, Capture One's previews, DaVinci Resolve's render cache, the media cache Adobe's video and audio applications share, the Squirrel updater's staging and the builds it superseded, the Dart analysis
    /// server's byte store, Roslyn's solution indexes, the Azure Functions Core Tools releases Visual Studio downloads, the driver packages graphics driver installers leave behind, what
    /// Claude Code's sessions leave behind, its conversations, its rewind snapshots and the logs of the MCP servers it runs, the
    /// caches, installer downloads and logs named tools leave in the temporary folders, the
    /// per-volume Recycle Bins, the Windows File History target, the crash
    /// dumps, the Windows servicing logs, the Windows component store's own cleanup and its reset, and the per-project build output, Unreal's included, inside the user's own
    /// approved folders — which the audit did not cover, and which were investigated on their own
    /// terms before being added. Their reasoning and their rejected alternatives are in
    /// <c>docs/cache-locations.md</c>.
    ///
    /// Tier 1 throughout except Unity, Unreal's per-project intermediate files and derived data, Cargo's per-project target, node_modules, Python virtual
    /// environments, conda, Maven, vcpkg, Steam's shader caches, the shared Unreal Engine derived data cache, Affinity's models, Capture One's previews, DaVinci Resolve's render cache, PlatformIO, Playwright, Puppeteer, the Azure Functions Core Tools
    /// releases, the graphics driver installer files, the installer downloads in the temporary folders, the superseded Squirrel builds, the local copies of cloud files and the component store's cleanup, which are Tier 2, and the
    /// Recycle Bins, the component store's reset, the File History target, the crash dumps, the servicing logs, the Epic
    /// launcher's logs, the VS Code logs, the tool logs in the temporary folders, and Claude Code's conversations, rewind snapshots and MCP server logs, which are Tier 3. Neither tier is ever
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
    /// <param name="liveTrees">
    /// What the providers ask about programs that are running. The shared inspector by default,
    /// whose snapshot of the process table a planning pass clears at its start. Explore passes its
    /// own, for the reason <paramref name="environment"/> gives.
    /// </param>
    /// <param name="environment">
    /// The machine the providers read through, which remembers where each command is on <c>PATH</c>
    /// until a planning pass clears it. <see cref="UserEnvironment.Current"/> by default. Explore
    /// passes a fresh one, beside a fresh inspector, for each policy it builds, because that build is
    /// a look at the machine of its own: clearing the shared answers would change what a Storage pass
    /// already under way sees, and reusing them would say where a command was the first time anything
    /// looked for it after the last Storage pass.
    /// </param>
    /// <param name="tuning">
    /// The route and the read sizes the user chose on the Settings page, which every provider's
    /// measurement runs with. Without it every provider shares <see cref="DirectoryScanner.Default"/>
    /// at the shipped values, as a test or the Explore page's provider list wants.
    /// </param>
    public static CleanupPlanner CreateDefault(
        ICurrentPreferences? preferences = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        ScanTuner? tuning = null)
    {
        environment ??= UserEnvironment.Current;
        var roots = new SourceRootStore(environment);

        // One scanner for every provider, as DirectoryScanner.Default is one: its volume index is the
        // whole cost of the fast path, and an unshared scanner per provider would rebuild it for each.
        var scanner = tuning is null ? DirectoryScanner.Default : DirectoryScanner.CreateDefault(environment, tuning);
        var hardLinks = tuning is null ? HardLinkAwareScanner.Default : new HardLinkAwareScanner(tuning);

        // One discovery for every provider that searches the user's own folders, and one live-tree
        // inspector beside it. Shared deliberately rather than defaulted per provider: six unshared
        // passes would each walk the developer's whole disk on an unelevated run, and the names each
        // provider registers on the way in are what make the one pass answer for all of them.
        var sourceTrees = new SourceDirectoryDiscovery(scanner);
        liveTrees ??= LiveTreeInspector.Default;

        // One sweep of %LOCALAPPDATA% for both Squirrel providers, on the reasoning above: they ask
        // the same question of the same directory, and two unshared discoveries would list a folder
        // holding hundreds of children twice per pass.
        var squirrel = new SquirrelDiscovery(environment);

        // One reading of Claude Code's list of running sessions for every Claude Code provider, on the same
        // reasoning: every entry in it costs a process probe, and unshared registries would ask about each
        // running process once per provider per pass.
        var claudeSessions = new ClaudeCodeSessionRegistry(environment, ProcessInspector.Default);

        // One walk over Claude Code's project folders for the two providers inside them, on the same
        // reasoning: the leftovers row asks which sessions have a conversation, and the conversations row
        // offers them, and the folder holds thousands of entries.
        var claudeProjects = new ClaudeCodeProjectsDiscovery(environment);

        // One finding of Steam for both providers inside its folders, on the same reasoning: each
        // asks the registry and probes the install, and the shader cache also reads the library list.
        var steam = new SteamDiscovery(environment);

        // One finding of RetroArch for both its rows, on the same reasoning: each reads the same settings
        // file, and a copy installed through Steam is found through the discovery above.
        var retroArch = new RetroArchDiscovery(environment, steam);

        // What every row declares it removes, read as one by each row that walks a folder another row
        // may also walk, so neither asserts that the other's removals survive.
        var declarations = new RowDeclarations();

        return new CleanupPlanner(
        [
            new DotNetObjProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new UnityLibraryProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new UnrealIntermediateProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new UnrealProjectDerivedDataProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new CargoTargetProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new NodeModulesProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            new PythonVirtualEnvironmentProvider(roots, sourceTrees, liveTrees, environment, scanner: scanner),
            .. CacheProviders(
                environment,
                squirrel,
                steam,
                retroArch,
                claudeSessions,
                claudeProjects,
                liveTrees,
                preferences ?? DefaultPreferences.Instance,
                declarations,
                scanner,
                hardLinks,
                tuning ?? ScanTuner.Shipped),
        ],
        declarations);
    }

    private static IReadOnlyList<ICleanupProvider> CacheProviders(
        IUserEnvironment environment,
        SquirrelDiscovery squirrel,
        SteamDiscovery steam,
        RetroArchDiscovery retroArch,
        ClaudeCodeSessionRegistry claudeSessions,
        ClaudeCodeProjectsDiscovery claudeProjects,
        ILiveTreeInspector liveTrees,
        ICurrentPreferences preferences,
        RowDeclarations declarations,
        IDirectoryScanner scanner,
        IDirectoryScanner hardLinks,
        ScanTuner tuning)
    {
        // Every row that offers an entry of a temporary folder under the name of the tool that wrote
        // it, built first so the "Temporary files" row can leave those entries to them.
        var nuget = new NuGetCacheProvider(environment, scanner: scanner);
        var toolCaches = new TempToolCacheProvider(environment, liveTrees: liveTrees, scanner: scanner);
        var installerDownloads = new TempInstallerDownloadProvider(environment, liveTrees: liveTrees, scanner: scanner);
        var toolLogs = new TempToolLogProvider(environment, liveTrees: liveTrees, scanner: scanner);
        var testBrowsers = new TestBrowserProfileProvider(environment, liveTrees: liveTrees, preferences: preferences, scanner: scanner);
        var afterEffects = new AfterEffectsDiskCacheProvider(environment, scanner: scanner);
        var claudeSnapshots = new ClaudeCodeCommandSnapshotProvider(
            environment, liveTrees: liveTrees, sessions: claudeSessions, scanner: scanner);

        // The routes that hand Windows a whole volume or a cloud account are given here and nowhere else
        // in the product. A provider takes each as a required argument, so a test builds one only by
        // naming a stand-in, and never inherits the one that empties its runner's Recycle Bin.
        var handlers = DiskCleanupHandlers.Default;

        // One analysis of the component store for both its rows: the analysis takes a minute or more,
        // and the cleanup and the reset ask it the same question.
        var componentStore = new ComponentStoreAnalysis(SystemDirectories.Current, ProcessRunner.Default);

        // One walk for the two rows inside every Chromium user-data folder, on the same reasoning: each
        // lists every directory below both application-data roots to find the folders, and the folders
        // are the same for both.
        var chromium = new ChromiumUserDataDiscovery(environment, tuning);

        return
        [
            nuget,
            new GradleCacheProvider(environment, declarations: declarations, scanner: scanner),
            new NpmCacheProvider(environment, scanner: scanner),
            new PnpmStoreProvider(environment, scanner: hardLinks),
            new VsCodeCppToolsCacheProvider(environment, scanner: scanner),
            new DartAnalysisServerProvider(environment, declarations: declarations, scanner: scanner),
            new RoslynCacheProvider(environment, scanner: scanner),
            toolCaches,
            new UvCacheProvider(environment, scanner: scanner),
            new PipCacheProvider(environment, scanner: scanner),
            new PoetryCacheProvider(environment, declarations: declarations, scanner: scanner),
            new CondaCacheProvider(environment, scanner: scanner),
            new CargoCacheProvider(environment, scanner: scanner),
            new GoCacheProvider(environment, scanner: scanner),
            new ZigCacheProvider(environment, scanner: scanner),
            new MavenRepositoryProvider(environment, scanner: scanner),
            new VcpkgCacheProvider(environment, scanner: scanner),
            new GpuShaderCacheProvider(environment, scanner: scanner),
            new ChromiumCacheProvider(environment, liveTrees: liveTrees, discovery: chromium, declarations: declarations, scanner: scanner),
            new ChromiumServiceWorkerStorageProvider(
                environment, liveTrees: liveTrees, discovery: chromium, declarations: declarations,
                scanner: scanner),
            new VsCodeCacheProvider(environment, declarations: declarations, scanner: scanner),
            new FirefoxCacheProvider(environment, scanner: scanner),
            new EpicLauncherWebCacheProvider(environment, declarations: declarations, scanner: scanner),
            new EpicLauncherContentCacheProvider(environment, scanner: scanner),
            new BattleNetCacheProvider(environment, scanner: scanner),
            new SteamCacheProvider(environment, discovery: steam, scanner: scanner),
            new SteamLibraryArtworkProvider(environment, discovery: steam, scanner: scanner),
            new SteamShaderCacheProvider(environment, discovery: steam, scanner: scanner),
            new EmulatorShaderCacheProvider(environment, scanner: scanner),
            new RetroArchDownloadProvider(environment, discovery: retroArch, scanner: scanner),
            new RetroArchThumbnailProvider(environment, discovery: retroArch, scanner: scanner),
            new UnrealDerivedDataCacheProvider(environment, scanner: scanner),
            new SpotifyCacheProvider(environment, scanner: scanner),
            new PlexTranscodeProvider(environment, scanner: scanner),
            new JellyfinTranscodeProvider(environment, scanner: scanner),
            new EmbyTranscodeProvider(environment, scanner: scanner),
            new AffinityModelCacheProvider(environment, scanner: scanner),
            new CaptureOneCacheProvider(environment, liveTrees: liveTrees, scanner: scanner),
            new ResolveRenderCacheProvider(environment, scanner: scanner),
            afterEffects,
            new AdobeMediaCacheProvider(environment, scanner: scanner),
            new SquirrelStagingProvider(environment, discovery: squirrel, liveTrees: liveTrees, scanner: scanner),
            new PlatformIoCacheProvider(environment, scanner: scanner),
            new PlaywrightBrowsersProvider(environment, scanner: scanner),
            new PuppeteerBrowsersProvider(environment, liveTrees: liveTrees, scanner: scanner),
            new LmStudioRuntimeProvider(environment, scanner: scanner),
            testBrowsers,
            new SquirrelSupersededVersionProvider(environment, discovery: squirrel, liveTrees: liveTrees, scanner: scanner),
            new AzureFunctionsToolsProvider(environment, scanner: scanner),
            new GraphicsDriverInstallerProvider(environment, liveTrees: liveTrees, scanner: scanner),
            new AutodeskInstallerProvider(environment, liveTrees: liveTrees, scanner: scanner),
            new ClaudeCodeDerivedStateProvider(
                environment, projects: claudeProjects, sessions: claudeSessions, declarations: declarations,
                scanner: scanner),
            claudeSnapshots,
            new RecycleBinProvider(ShellRecycleBinEmptier.Default, environment, preferences: preferences, scanner: scanner),
            new FileHistoryProvider(environment, preferences: preferences, scanner: scanner),
            new CloudLocalCopiesProvider(CloudFiles.Default, environment, scanner: scanner),
            new TempDirectoryProvider(
                environment,
                liveTrees: liveTrees,
                preferences: preferences,
                tenants: [nuget, toolCaches, installerDownloads, toolLogs, testBrowsers, afterEffects, claudeSnapshots],
                scanner: scanner),
            installerDownloads,
            new DeliveryOptimizationProvider(environment, scanner: scanner),
            new PreviousWindowsInstallationProvider(handlers, environment, scanner: scanner),
            new WindowsUpdateLeftoverProvider(environment, scanner: scanner),
            new DriverStoreProvider(handlers, environment, scanner: hardLinks),
            new ComponentStoreCleanupProvider(environment, analysis: componentStore, scanner: scanner),
            new ComponentStoreResetBaseProvider(environment, analysis: componentStore, scanner: scanner),
            new RestorePointProvider(WindowsSystemProtection.Default, environment, scanner: scanner),
            new CrashDumpProvider(environment, scanner: scanner),
            new WindowsServicingLogProvider(handlers, environment, scanner: scanner),
            new EpicLauncherLogProvider(environment, scanner: scanner),
            new BattleNetLogProvider(environment, scanner: scanner),
            new VsCodeLogProvider(environment, declarations: declarations, scanner: scanner),
            new ClaudeCodeMcpLogProvider(environment, scanner: scanner),
            toolLogs,
            new ClaudeCodeFileHistoryProvider(environment, sessions: claudeSessions, scanner: scanner),
            new ClaudeCodeConversationProvider(environment, projects: claudeProjects, sessions: claudeSessions, scanner: scanner),
            new VirtualDiskProvider(SystemDirectories.Current, environment, scanner: scanner),
        ];
    }

    public IReadOnlyList<ICleanupProvider> Providers => _providers;

    /// <summary>The declarations this planner admitted, so a test can hold every row that walks a folder to them.</summary>
    internal RowDeclarations? Declarations => _declarations;

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
    public Task<IReadOnlyList<Finding>> PlanAllAsync(
        MinimumAge keep = default,
        IProgress<string>? status = null,
        IProgress<Finding>? found = null,
        CancellationToken ct = default) =>
        PlanAsync(_providers, keep, status, found, ct);

    /// <summary>
    /// Preview <paramref name="providers"/> alone, on the same terms <see cref="PlanAllAsync"/> previews
    /// every one: their caches dropped first, one at a time, each reported as it lands, largest first.
    ///
    /// <para>For the re-plan after a clean, which only has to describe the locations the run changed.
    /// Every other provider's finding still describes the disk, and measuring it again costs a walk of
    /// its tree for an answer already on screen. See <see cref="RunChanges"/> for which those are.</para>
    /// </summary>
    public async Task<IReadOnlyList<Finding>> PlanAsync(
        IReadOnlyList<ICleanupProvider> providers,
        MinimumAge keep = default,
        IProgress<string>? status = null,
        IProgress<Finding>? found = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(providers);

        // Every provider drops its cached view of the machine before any of them plans. Doing
        // this up front rather than per-provider matters: the providers share collaborators by
        // default, so invalidating inside the loop would throw away the snapshot the previous
        // provider just paid for.
        foreach (var provider in providers)
        {
            provider.InvalidateCaches();
        }

        _declarations?.Invalidate();

        var findings = new List<Finding>(providers.Count);

        foreach (var provider in providers)
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
    /// §7 makes deleting the deliberate second step. A plan whose requirement is unmet throws, before
    /// any plan runs, rather than being skipped: silently dropping it would report success for work not
    /// done.
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
    /// <param name="ct">
    /// Stops the run. <b>Never thrown:</b> the results hold every plan, the ones that finished, the
    /// one that was stopped and the ones never started, each <see cref="CleanupResult.Interrupted"/>
    /// where it did not finish and each verified (§5.6). Unlike planning, a cancelled clean has
    /// already changed the disk, and the results are the only account of how.
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

        // What the run's removals leave standing and leave alone, written by each plan as it executes
        // and read by every verification after it. One record for the whole run, for the reason the
        // reach is one value: a folder one provider's removal went into may be a folder another
        // provider promised to leave. See RunResidue.
        var residue = new RunResidue();

        // §7's extra confirmation for anything above Tier 1, for every plan before the first deletion.
        // The requirement is derived here rather than trusted from the caller: a shell that forgot to
        // ask, or asked for the wrong subject, must fail closed rather than delete. Asked inside the
        // loop, the refusal of a later plan arrived after an earlier one had deleted, and threw away
        // that plan's result and its §5.6 verdict.
        //
        // A plan with no steps is not asked about at any tier. It destroys nothing, so there is
        // nothing to authorise, and failing closed on it would refuse the run over a check that only
        // reads the disk.
        foreach (var (_, plan) in plans.Where(p => !p.Plan.IsEmpty))
        {
            var requirement = ConfirmationRequirement.For(plan, requireTypedPhrase);

            if (!requirement.IsSatisfiedBy(confirmations))
            {
                throw new ConfirmationRequiredException(requirement);
            }
        }

        var weights = Weigh([.. plans.Select(p => p.Plan)]);
        var total = weights.Sum();
        var results = new List<CleanupResult>(plans.Count);
        var done = 0.0;
        var cancelled = false;

        for (var i = 0; i < plans.Count; i++)
        {
            var (finding, plan) = plans[i];

            // A plan that deletes is not started once the clean is cancelled, and is only verified. See
            // ProveUnstartedAsync. A plan that only proves is run whatever happened, with a token that
            // cannot be cancelled: its promise is about what the run leaves standing, and a cancelled
            // run is the one that most needs it kept.
            if (!plan.IsEmpty && (cancelled || ct.IsCancellationRequested))
            {
                cancelled = true;
                results.Add(await ProveUnstartedAsync(finding, plan, reach, residue).ConfigureAwait(false));
                continue;
            }

            status?.Report(plan.IsEmpty
                ? $"Checking what {finding.Provider.Name} left alone…"
                : $"Cleaning {finding.Provider.Name}…");

            var result = await finding.Provider
                .ExecuteAsync(
                    plan,
                    reach,
                    residue,
                    ScaledProgress.Within(progress, done / total, weights[i] / total),
                    plan.IsEmpty ? CancellationToken.None : ct)
                .ConfigureAwait(false);

            results.Add(result);
            cancelled |= result.Interrupted;

            // Reported from here rather than trusted from the provider: a provider that reports
            // nothing would otherwise leave the bar wherever the previous one left it, and one that
            // stops short of 1 would leave a gap that never closes.
            done += weights[i];
            progress?.Report(done / total);
        }

        return results;
    }

    /// <summary>
    /// The result of a plan the clean was cancelled before starting: no steps, and the §5.6 check of
    /// everything it protects. An earlier plan's over-reach is what could have taken one of those
    /// paths, and in a run that finished this plan's own verification would have been what said so.
    /// </summary>
    private static async Task<CleanupResult> ProveUnstartedAsync(
        Finding finding,
        CleanupPlan plan,
        RunReach reach,
        RunResidue residue) => new()
    {
        ProviderId = plan.ProviderId,
        ProviderName = plan.ProviderName,
        Interrupted = true,
        Verification = await finding.Provider
            .VerifyAsync(plan, reach, residue, CancellationToken.None)
            .ConfigureAwait(false),
    };

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
