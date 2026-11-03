using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Bun's global install cache. Researched rather than measured: no Bun was installed on the machine
/// this was written against, so every behaviour here is read from oven-sh/bun at bun-v1.4.4.
///
/// <para><b>§5.1 through <c>prune</c> alone.</b> Bun has two evictions, and only one of them is a
/// cache clean. <c>bun pm cache rm</c> deletes everything in the cache, <c>links</c> included, and
/// <c>links</c> is the global store: with <c>install.globalStore</c> on, a project's
/// <c>node_modules</c> are links into it, and removing it breaks each such project until it is
/// installed again (<a href="https://bun.com/docs/pm/global-store">global store</a>).
/// <c>bun pm cache prune</c> removes only the extracted package folders and keeps <c>links</c>, git
/// clones, the transpiler cache, manifests and anything it does not recognise
/// (<see cref="BunCacheEntries"/>). So the plan runs <c>prune</c> and never <c>rm</c>.</para>
///
/// <para><b>Bun 1.4.4 or later, and nothing for an earlier one.</b> <c>prune</c> first shipped in
/// 1.4.4, and an earlier Bun has no eviction that keeps <c>links</c>, which it has had since 1.3.14.
/// Deleting the package folders by path instead would need the cache's location, which an earlier Bun
/// will not report from a folder with no <c>package.json</c>, and would rest on how 1.3.14 to 1.4.3
/// link that store, which nothing read establishes. The row says to update Bun instead.</para>
///
/// <para><b>Measured link-aware.</b> Bun installs on Windows by hard-linking a package's files out of
/// the cache, so removing a package folder frees only the files no <c>node_modules</c> still links,
/// and that is what <see cref="HardLinkAwareScanner"/> counts, as for pnpm.</para>
///
/// <para><b>Tier 1.</b> Every package removed is one the registry still serves, and an installed
/// project keeps its own links to the files.</para>
/// </summary>
public sealed class BunInstallCacheProvider : CleanupProviderBase
{
    private const string HomeReason =
        "This is Bun's own folder: Bun itself, the packages you installed globally, and its install cache. "
        + "Deguffer removes nothing here from Explore except package folders at the top of the cache.";

    private const string CacheReason =
        "This is Bun's install cache. Deguffer clears it with Bun's own prune, from the Storage page, and "
        + "leaves the links store, which projects run from, and anything else it does not recognise.";

    private readonly ISystemDirectories _system;
    private readonly PassMemo<Task<BunInstall?>> _install = new();

    public BunInstallCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? HardLinkAwareScanner.Default,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "bun";

    public override string Name => "Bun install cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next bun install downloads the packages it needs again. Installed node_modules, your global "
        + "packages and Bun's global store of linked packages are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Bun, a JavaScript runtime and package manager",
        Publisher = "Oven, the company behind Bun",
        Purpose = "Bun keeps every package it installs in one cache shared by all your projects, and links "
            + "the files from there into each project, so installing the same version again is served from disk.",
        Recommendation = "Deguffer runs Bun's own cache prune, which keeps the global store your projects may "
            + "run from. It needs Bun 1.4.4 or later.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["bun"];

    /// <summary>
    /// Bun's home, moved by <c>BUN_INSTALL</c>: Bun itself, <c>install\global</c> and, by default, the
    /// cache (<c>PackageManagerDirectories::fetch_cache_directory_path</c>).
    /// </summary>
    public string HomeDirectory =>
        LongPath.Configured(Environment.GetEnvironmentVariable("BUN_INSTALL"))
            ?? Path.Combine(Environment.UserProfile, ".bun");

    /// <summary>The cache in <see cref="HomeDirectory"/>, where nothing else moves it.</summary>
    public string DefaultCache => Path.Combine(HomeDirectory, "install", "cache");

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside. Bun's home recognises nothing, and the cache recognises
    /// the package folders prune removes, never <c>links</c>.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. ToolRoot.WayDown(HomeDirectory, [DefaultCache], HomeReason),
        CacheRoot(DefaultCache),
    ];

    /// <summary>The cache Bun reports, where its prune would be run there.</summary>
    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        await AcceptedCacheAsync(ct).ConfigureAwait(false) is { } cache
            ? [CacheRoot(cache)]
            : [];

    /// <summary>The default cache and the one Bun reports, because a setting can move it back after a duplicate search.</summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        List<string> caches = [DefaultCache];

        if (await AcceptedCacheAsync(ct).ConfigureAwait(false) is { } cache)
        {
            caches.Add(cache);
        }

        return [.. caches.Distinct(StringComparer.OrdinalIgnoreCase).Select(CleanedPlace.Whole)];
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("bun") is not null);

    /// <summary>The version and the cache's location both change without Deguffer: an upgrade, a setting.</summary>
    public override void InvalidateCaches()
    {
        _install.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (Environment.FindExecutable("bun") is not { } bun)
        {
            return EmptyPlan("Bun is not installed on this machine.");
        }

        if (await ResolveAsync(bun, ct).ConfigureAwait(false) is not { } install)
        {
            return UnexaminedPlan("Bun did not say which version it is, so Deguffer cannot tell whether it can prune its cache.");
        }

        if (!install.CanPrune)
        {
            return EmptyPlan(
                $"Bun {install.Version} cannot prune its cache: Bun added that in {BunInstall.FirstWithPrune}. Its only "
                + "other way to clear the cache also deletes the global store projects can run from, so Deguffer "
                + "leaves it alone. Run bun upgrade and scan again.");
        }

        if (install.Cache is not { } cache)
        {
            return UnexaminedPlan(install.Reported is { } reported
                ? $"Bun reported its cache as '{reported}', which is not a full path, so Deguffer cannot tell what its prune would remove."
                : "Bun did not say where its cache is, so Deguffer cannot tell what its prune would remove.");
        }

        if (WhyNotPruned(cache) is { } declined)
        {
            return EmptyPlan(declined);
        }

        if (NothingToPlanFor(cache, $"Bun is installed but has cached nothing yet ({cache} does not exist).") is { } nothing)
        {
            return nothing;
        }

        if (LongPath.IsReparsePoint(cache))
        {
            return EmptyPlan($"Leaving '{cache}' alone: it is a link to somewhere else, and Deguffer does not look through a link.");
        }

        if (BunCacheEntries.Of(cache) is not { } contents)
        {
            return UnreadableRootPlan(cache);
        }

        if (contents.Strangers.FirstOrDefault() is { } stranger)
        {
            return EmptyPlan(
                $"Bun's cache {cache} holds '{stranger}', which is not laid out as Bun's own, and Bun's prune would "
                + "delete or empty it all the same. Deguffer is leaving the cache alone.");
        }

        var packages = contents.Packages;

        if (packages.Count == 0)
        {
            return EmptyPlan($"Bun's cache holds no package its prune removes ({cache}).");
        }

        var measured = await MeasureAllAsync(packages, ct).ConfigureAwait(false);

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information,
                $"Bun {install.Version} reports its cache as {cache}. Its prune removes the {packages.Count} package "
                + $"folders there and keeps {BunCacheEntries.LinksName}, git clones and package listings. Files "
                + "hard-linked into a project's node_modules are neither counted nor freed."),
        };

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (BuildRunningProcessNote() is { } warning)
        {
            notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps =
            [
                new RunCommandStep(bun, BunInstall.PruneArguments(cache), "Prune the Bun install cache using Bun's own command")
                {
                    // Prune deletes one package folder at a time, and Bun reads a package as cached only
                    // where its folder is whole, so nothing found shows that a folder cut off part-way is
                    // noticed. A prune is quick, so it is left to finish.
                    WhenCancelled = CancelledCommand.Finishes,
                    Estimated = measured.Total,
                    MeasuredPaths = packages,
                },
            ],
            ProtectedPaths = Protect(
            [
                (cache, "Bun's prune removes package folders inside its cache and keeps the folder."),
                (Path.Combine(cache, BunCacheEntries.LinksName),
                    "Bun's global store, which projects' node_modules can link into. Prune keeps it."),
                (HomeDirectory, "Bun's home, which holds Bun itself."),
                (Path.Combine(HomeDirectory, "install", "global"), "The packages you installed globally with Bun."),
                .. contents.Kept
                    .Where(kept => !kept.Equals(Path.Combine(cache, BunCacheEntries.LinksName), StringComparison.OrdinalIgnoreCase))
                    .Select(kept => (kept, "Bun's prune keeps this: a git clone, a package listing, its transpiler cache or something it does not recognise.")),
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            CountsLinksFromElsewhere = true,
        };
    }

    /// <summary>
    /// Why prune must not be sent to the cache Bun reports, as a sentence, or null where it may. Bun
    /// refuses a cache that holds the profile or itself, and this refuses every folder
    /// <see cref="ConfiguredFolder"/> declines as well.
    /// </summary>
    private string? WhyNotPruned(string cache) =>
        ConfiguredFolder.WhyNotOwned(
            cache, Environment, _system, Volumes, TempRoots.Resolve(Environment, _system, Volumes).AccountFolders) is { } notOwned
            ? $"Bun is set to keep its cache in '{cache}', and Deguffer will not run its prune there: {notOwned} It is leaving it alone."
            : null;

    /// <summary>The cache, recognising the package folders prune removes and nothing it keeps.</summary>
    private static ToolRoot CacheRoot(string cache) =>
        ToolRoot.Folders(cache, CacheReason, name => BunCacheEntries.IsPackage(cache, name));

    private async Task<string?> AcceptedCacheAsync(CancellationToken ct) =>
        Environment.FindExecutable("bun") is { } bun
        && await ResolveAsync(bun, ct).ConfigureAwait(false) is { CanPrune: true, Cache: { } cache }
        && WhyNotPruned(cache) is null
            ? cache
            : null;

    private Task<BunInstall?> ResolveAsync(string bun, CancellationToken ct) =>
        _install.Get(() => BunInstall.AskAsync(Runner, bun, ct));
}
