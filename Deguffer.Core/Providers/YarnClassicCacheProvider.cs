using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Yarn 1's package cache. Researched rather than measured: no Yarn was installed on the machine this
/// was written against, so every behaviour here is read from yarnpkg/yarn at v1.22.22.
///
/// <para><b>§5.1, and only for Yarn 1.</b> <c>yarn cache clean</c> with no package named deletes the
/// whole cache root, the folder holding the versioned <c>v6</c> that <c>yarn cache dir</c> reports,
/// then makes an empty <c>v6</c> again
/// (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/cli/commands/cache.js#L119-L124">cache.js</a>).
/// Yarn 2 and later answer to the same name, and their <c>yarn cache clean</c> deletes the global
/// cache Plug'n'Play projects run from, so the plan asks the program which Yarn it is (see
/// <see cref="YarnClassicCache"/>) and offers nothing for any other. That cache is
/// <see cref="YarnBerryCacheProvider"/>'s row.</para>
///
/// <para><b>Everything in the root goes, so the root is vetted first.</b> A <c>cache-folder</c>
/// setting can name any folder, and the clean would delete the whole of it. The plan runs it only on
/// a root <see cref="ConfiguredFolder"/> accepts, that holds neither Yarn's data folder nor Berry's,
/// and that holds nothing but Yarn's versioned cache folders. Anything else there is §5.2's
/// unrecognised.</para>
///
/// <para><b>Tier 1.</b> Every entry is a package the registry still serves, fetched again at the next
/// install that needs it.</para>
/// </summary>
public sealed class YarnClassicCacheProvider : CleanupProviderBase
{
    private const string ToolFolderReason =
        "This is Yarn's own folder. Its Data folder holds the packages you installed globally and the "
        + "packages you linked, and Yarn 2 and later keep their global folder here too. Deguffer removes "
        + "nothing here except Yarn 1's cache and, in a row of its own, the global cache of Yarn 2 and later.";

    private const string CacheRootReason =
        "This is Yarn 1's cache. Deguffer clears it with Yarn's own cache clean, from the Storage page, "
        + "and leaves anything here it does not recognise.";

    private const string ConfigurationReason =
        "This is Yarn's configuration, and it may hold registry authentication tokens. Deguffer never removes it.";

    private readonly ISystemDirectories _system;
    private readonly PassMemo<Task<YarnClassicCache?>> _cache = new();
    private readonly ToolAnswerMemory _memory;

    /// <param name="environment">The profile, the executables and the variables Yarn is started with.</param>
    /// <param name="runner">Starts Yarn.</param>
    /// <param name="inspector">Tells whether Yarn or Node.js is running.</param>
    /// <param name="scanner">Measures the cache.</param>
    /// <param name="system">Windows' own folders, which a configured cache root must not be.</param>
    /// <param name="volumes">The volumes, which a configured cache root must not be the root of.</param>
    /// <param name="memory">
    /// Where Yarn's answers about which Yarn it is and where its cache is are kept while nothing Yarn
    /// read has changed: the memory for <paramref name="runner"/> when null, which is the process-wide
    /// one for the real runner.
    /// </param>
    public YarnClassicCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null,
        ToolAnswerMemory? memory = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _memory = memory ?? ToolAnswerMemory.For(runner);
    }

    public override string Id => "yarn";

    public override string Name => "Yarn 1 package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next yarn install downloads its packages from the registry again. Installed node_modules, "
        + "your global packages and Yarn 2 and later's caches are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Yarn 1 (Classic), a package manager for Node.js",
        Publisher = "the open-source Yarn project",
        Purpose = "Yarn 1 keeps an unpacked copy of every package it downloads in one cache shared by all "
            + "your projects, so installing the same version again is served from disk.",
        Recommendation = "Deguffer runs Yarn's own cache clean, and only where the yarn on your PATH is "
            + "Yarn 1. Yarn 2 and later keep a cache that projects run from, which Deguffer offers in a row of its own.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["node", "yarn"];

    /// <summary>
    /// Yarn's folder on Windows: the default cache root, <c>Data</c> and Berry's folder are all in it
    /// (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/util/user-dirs.js">user-dirs.js</a>,
    /// <a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/yarnpkg-core/sources/folderUtils.ts">folderUtils.ts</a>).
    /// </summary>
    public string ToolFolder => Path.Combine(Environment.LocalAppData, "Yarn");

    /// <summary>Yarn 1's cache root where nothing moves it.</summary>
    public string DefaultCacheRoot => Path.Combine(ToolFolder, "Cache");

    /// <summary>Where Yarn 1 keeps <c>yarn global</c> and <c>yarn link</c>: user state, never cache.</summary>
    public string DataFolder => Path.Combine(ToolFolder, "Data");

    /// <summary>The global folder of Yarn 2 and later, whose cache Plug'n'Play projects run from.</summary>
    public string BerryFolder => YarnBerryCache.DefaultGlobalFolder(Environment);

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside. Yarn's folder recognises its cache root alone, the root
    /// recognises its versioned folders alone, and the configuration files in the profile recognise
    /// nothing.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        ToolRoot.Folders(ToolFolder, ToolFolderReason, static name => name.Equals("Cache", StringComparison.OrdinalIgnoreCase)),
        ToolRoot.Folders(DefaultCacheRoot, CacheRootReason, YarnClassicCache.IsVersionFolder),
        .. ConfigurationFiles.Select(file => new ToolRoot(file, ConfigurationReason, static _ => false)),
    ];

    /// <summary>The cache root Yarn 1 reports, where its clean would be run there.</summary>
    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        await AcceptedRootAsync(ct).ConfigureAwait(false) is { } root
            ? [ToolRoot.Folders(root, CacheRootReason, YarnClassicCache.IsVersionFolder)]
            : [];

    /// <summary>
    /// The default root and the one Yarn 1 reports, because a setting can move the cache back to the
    /// default after a duplicate search has asked.
    /// </summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        List<string> roots = [DefaultCacheRoot];

        if (await AcceptedRootAsync(ct).ConfigureAwait(false) is { } root)
        {
            roots.Add(root);
        }

        return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase).Select(CleanedPlace.Whole)];
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("yarn") is not null);

    /// <summary>
    /// Which Yarn runs, and where its cache is, are both configuration that can change between scans.
    /// The pass's answer is forgotten here, and the memory keeps Yarn's answers only while everything
    /// <see cref="YarnToolInputs"/> declares, and the environment Yarn is started with, read the same.
    /// </summary>
    public override void InvalidateCaches()
    {
        _cache.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (Environment.FindExecutable("yarn") is not { } yarn)
        {
            return EmptyPlan("Yarn is not installed on this machine.");
        }

        if (await ResolveAsync(yarn, ct).ConfigureAwait(false) is not { } answer)
        {
            return UnexaminedPlan("Yarn did not say which version it is, so Deguffer cannot tell whether its cache clean is Yarn 1's.");
        }

        if (answer.Location is not { } location)
        {
            return EmptyPlan(
                $"The yarn on your PATH runs Yarn {answer.Version.Text}, not Yarn 1. Yarn 2 and later keep a cache "
                + "Plug'n'Play projects run from, which Deguffer offers in its Yarn global package cache row.");
        }

        if (location.Root is not { } root)
        {
            return UnexaminedPlan(location.Reported is { } reported
                ? $"Yarn reported its cache as '{reported}', which is not a full path to a versioned cache folder, so "
                    + "Deguffer cannot tell what its cache clean would remove."
                : "Yarn did not say where its cache is, so Deguffer cannot tell what its cache clean would remove.");
        }

        if (WhyNotCleared(root) is { } declined)
        {
            return EmptyPlan(declined);
        }

        if (NothingToPlanFor(root, $"Yarn is installed but has cached nothing yet ({root} does not exist).", out var rootIsLink) is { } nothing)
        {
            return nothing;
        }

        switch (WhyNotRemoved(root, rootIsLink))
        {
            case { Unlisted: true }:
                return UnreadableRootPlan(root);
            case { Why: { } why }:
                return EmptyPlan(why);
        }

        var measured = await MeasureAllAsync([root], ct).ConfigureAwait(false);

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information,
                $"Yarn {answer.Version.Text} reports its cache as {location.Reported}. Its cache clean removes {root} and "
                + "makes an empty cache folder there again."),
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
                new RunCommandStep(yarn, YarnClassicCache.CleanArguments(root), "Clear the Yarn 1 cache using Yarn's own command")
                {
                    // Yarn 1 reads a package folder as cached by the metadata file in it, so one cut off
                    // part-way could be read as whole. A clean is one recursive delete, so it is left to finish.
                    WhenCancelled = CancelledCommand.Finishes,
                    Estimated = measured.Total,
                    MeasuredPaths = [root],
                },
            ],
            ProtectedPaths = await ProtectAsync(
                [
                    (DataFolder, "Yarn's data folder: the packages you installed globally and the packages you linked."),
                    (BerryFolder, "The global cache of Yarn 2 and later, which Plug'n'Play projects run from."),
                    .. ConfigurationFiles.Select(file => (file, "Yarn's configuration, which may hold registry authentication tokens.")),
                ],
                ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>Yarn 1's <c>.yarnrc</c> and Berry's <c>.yarnrc.yml</c> in the profile, each of which can hold a token.</summary>
    private IEnumerable<string> ConfigurationFiles =>
    [
        Path.Combine(Environment.UserProfile, ".yarnrc"),
        Path.Combine(Environment.UserProfile, ".yarnrc.yml"),
    ];

    /// <summary>
    /// Why the root Yarn 1 reported must not be deleted, as a sentence, or null where it may. Asked of
    /// the path, before anything on the disk.
    /// </summary>
    private string? WhyNotCleared(string root)
    {
        if (ConfiguredFolder.WhyNotOwned(
                root, Environment, _system, Volumes, TempRoots.Resolve(Environment, _system, Volumes).AccountFolders) is { } notOwned)
        {
            return $"Yarn is set to keep its cache in '{root}', and Deguffer will not run its cache clean there, "
                + $"which deletes the whole folder: {notOwned} It is leaving it alone.";
        }

        var folder = Reach(root);

        return new[] { DataFolder, BerryFolder }.Any(kept => folder.Holds(Reach(kept)))
            ? $"Yarn is set to keep its cache in '{root}', which holds Yarn's data folder or Berry's cache, and its "
                + "cache clean deletes the whole folder. Deguffer is leaving it alone."
            : null;
    }

    /// <summary>
    /// Why the root as it stands on the disk must not be deleted, or nothing where it may. The clean
    /// deletes every entry in it, so a link or anything but a versioned cache folder would go through
    /// a rule that cannot name it.
    /// </summary>
    /// <param name="isLink">Whether the root is a link, from the probe that found it there.</param>
    private static (bool Unlisted, string? Why) WhyNotRemoved(string root, bool isLink)
    {
        if (isLink)
        {
            return (false, $"Leaving '{root}' alone: it is a link to somewhere else, and Deguffer does not look through a link.");
        }

        if (FolderEntries.Of(root) is not { } entries)
        {
            return (true, null);
        }

        return entries.FirstOrDefault(entry =>
                entry is not DirectoryInfo
                || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || !YarnClassicCache.IsVersionFolder(entry.Name)) is { } stranger
            ? (false, $"Yarn's cache folder {root} holds '{stranger.Name}', which Yarn does not make, and its cache "
                + "clean would delete it with everything else. Deguffer is leaving the cache alone.")
            : (false, null);
    }

    private async Task<string?> AcceptedRootAsync(CancellationToken ct) =>
        Environment.FindExecutable("yarn") is { } yarn
        && await ResolveAsync(yarn, ct).ConfigureAwait(false) is { Location.Root: { } root }
        && WhyNotCleared(root) is null
            ? root
            : null;

    private Task<YarnClassicCache?> ResolveAsync(string yarn, CancellationToken ct) =>
        _cache.Get(() => AskAsync(yarn, ct));

    /// <summary>
    /// Which Yarn runs and, for Yarn 1, where its cache is, each through the memory, once per pass.
    /// The plan runs Yarn's own clean (§5.1), sent to a root it vets on the disk as it plans, so a
    /// remembered answer can at worst misstate the figure. Null where Yarn would not say which it is.
    /// </summary>
    private async Task<YarnClassicCache?> AskAsync(string yarn, CancellationToken ct)
    {
        if (await YarnVersion.AskAsync(_memory, Environment, Runner, yarn, ct).ConfigureAwait(false) is not { } version)
        {
            return null;
        }

        if (!version.IsClassic)
        {
            return new YarnClassicCache(version, Location: null);
        }

        var location = await _memory.AskAsync(
            new ToolQuestion<YarnClassicLocation>(
                $"cache dir;config get yarn-path|{yarn}",
                Environment.ToolEnvironment,
                answer => YarnToolInputs.Classic(Environment, yarn, answer.Script),
                static answer => answer.IsAnswer,
                askCt => YarnClassicLocation.AskAsync(Runner, yarn, askCt)),
            ct).ConfigureAwait(false);

        return new YarnClassicCache(version, location);
    }
}
