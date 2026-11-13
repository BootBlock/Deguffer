using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Composer's package cache: the archives, repository metadata and git mirrors PHP's package manager
/// downloads (30.1 MB on the machine this was written against, with Composer 2.4.4).
///
/// <para><b>§5.1, with a command that empties whatever it is pointed at.</b> <c>composer
/// clear-cache</c> empties the cache folder and the three folders inside it that its settings name,
/// deleting every entry it finds there. So the plan asks Composer for each setting (see
/// <see cref="ComposerSettings"/>) and runs the command only where every folder it empties is the cache
/// folder or one of its own three, and the cache folder holds nothing but those three and the
/// <c>.htaccess</c> Composer writes. Anything else there is §5.2's unrecognised, and the command
/// cannot be told to leave it, so the row offers nothing and says why.</para>
///
/// <para><b>Tier 1.</b> Every entry is a download Composer fetches again when an install needs it,
/// and Composer already expires its archives itself, past 300 MiB or six months
/// (<a href="https://getcomposer.org/doc/06-config.md#cache-files-ttl">config</a>).</para>
/// </summary>
public sealed class ComposerCacheProvider : CleanupProviderBase
{
    /// <summary>The file Composer writes into each of its folders to keep a web server out of it.</summary>
    private const string HtaccessName = ".htaccess";

    private const string HomeReason =
        "This is Composer's home, which holds its settings, the credentials it signs in to package "
        + "repositories with, and the packages you installed globally. Deguffer never removes anything here.";

    private const string CacheReason =
        "This is Composer's cache folder. Deguffer clears it with Composer's own clear-cache, from the "
        + "Storage page, and leaves anything here it does not recognise.";

    /// <summary>The folders inside the cache folder Composer's settings default to (<c>Config::$defaultConfig</c>).</summary>
    private static readonly DisposableChildSet CacheParts = new(
    [
        new ChildClassification("files", SafetyTier.RegenerableCache, "Package archives Composer downloaded."),
        new ChildClassification("repo", SafetyTier.RegenerableCache, "Package listings Composer downloaded from repositories."),
        new ChildClassification("vcs", SafetyTier.RegenerableCache, "Git mirrors of packages Composer installed from source."),
    ]);

    private readonly ISystemDirectories _system;
    private readonly PassMemo<Task<ComposerSettings?>> _settings = new();

    public ComposerCacheProvider(
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
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "composer";

    public override string Name => "Composer package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next composer install or update downloads its packages again. Every project's vendor folder, "
        + "your global packages and your Composer credentials are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Composer, the package manager for PHP",
        Publisher = "the open-source Composer project",
        Purpose = "Composer keeps a copy of every package archive, repository listing and git mirror it "
            + "downloads, so installing the same version again is served from disk.",
        Recommendation = "Deguffer runs Composer's own clear-cache. Composer already trims the package "
            + "archives in this cache itself past 300 MiB or six months, so it rarely grows large.",
    };

    /// <summary>Composer runs as PHP, so a running install or update is a <c>php</c> process.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["php"];

    /// <summary>Composer's home where nothing moves it, which holds its configuration and credentials.</summary>
    public string DefaultHome => Path.Combine(Environment.RoamingAppData, "Composer");

    /// <summary>Composer's cache folder on Windows where nothing moves it (<c>Factory::getCacheDir</c>).</summary>
    public string DefaultCache => Path.Combine(Environment.LocalAppData, "Composer");

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside. The home recognises nothing: <c>auth.json</c> there
    /// holds repository credentials, and the global packages and configuration are beside it. The cache
    /// recognises the three folders <c>clear-cache</c> empties. The folders Composer reports are
    /// declared in <see cref="DiscoverToolRootsAsync"/>, because only Composer can say.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        new ToolRoot(DefaultHome, HomeReason, static _ => false),
        ToolRoot.Of(DefaultCache, CacheReason, CacheParts),
    ];

    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        if (Environment.FindExecutable("composer") is not { } composer
            || await ResolveSettingsAsync(composer, ct).ConfigureAwait(false) is not { } settings)
        {
            return [];
        }

        List<ToolRoot> roots = [];

        foreach (var home in new[] { settings.Home.Folder, settings.DataDirectory.Folder }
                     .OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(new ToolRoot(home, HomeReason, static _ => false));
        }

        if (WhyNotCleared(settings) is null)
        {
            roots.Add(ToolRoot.Of(settings.Cache.Folder!, CacheReason, CacheParts));
        }

        return roots;
    }

    /// <summary>
    /// The default cache folder, and the one Composer reports where its clear-cache would be run there,
    /// because a setting can move it back to the default after a duplicate search has asked.
    /// </summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        List<string> caches = [DefaultCache];

        if (Environment.FindExecutable("composer") is { } composer
            && await ResolveSettingsAsync(composer, ct).ConfigureAwait(false) is { } settings
            && WhyNotCleared(settings) is null)
        {
            caches.Add(settings.Cache.Folder!);
        }

        return [.. caches.Distinct(StringComparer.OrdinalIgnoreCase).Select(CleanedPlace.Whole)];
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("composer") is not null);

    /// <summary>Every answer is configuration, which the user can change between one scan and the next.</summary>
    public override void InvalidateCaches()
    {
        _settings.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Composer's settings, asked as the pass begins: each is a PHP start of its own, seven in turn, and
    /// they are almost all of this row's wait. Nothing is asked where a <c>COMPOSER</c> project makes
    /// the plan refuse before it asks.
    /// </summary>
    public override Task BeginPass(PassStart start) =>
        start.Ask(ct => Environment.FindExecutable("composer") is { } composer
            && string.IsNullOrWhiteSpace(Environment.GetToolEnvironmentVariable("COMPOSER"))
                ? ResolveSettingsAsync(composer, ct)
                : Task.CompletedTask);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (Environment.FindExecutable("composer") is not { } composer)
        {
            return EmptyPlan("Composer is not installed on this machine.");
        }

        // Composer 2.11's clear-cache loads the project this names and takes its cache settings, which the
        // global settings asked here never include (ConfigCommand merges no project under --global), so
        // the clear could empty a folder nobody vetted.
        if (!string.IsNullOrWhiteSpace(Environment.GetToolEnvironmentVariable("COMPOSER")))
        {
            return EmptyPlan(
                "The COMPOSER environment variable names a project file, and Composer's clear-cache takes that "
                + "project's cache settings, which Deguffer cannot check from here. It is leaving the cache alone.");
        }

        if (await ResolveSettingsAsync(composer, ct).ConfigureAwait(false) is not { } settings)
        {
            return UnexaminedPlan(
                "Composer did not say where its cache folders are, and its clear-cache empties every one of "
                + "them, so Deguffer cannot tell what it would remove.");
        }

        if (settings.Cache.Disabled)
        {
            return EmptyPlan($"Composer's cache is turned off (cache-dir is '{settings.Cache.Reported}'), so it holds nothing.");
        }

        if (settings.ReadOnly)
        {
            return EmptyPlan("Composer is set to use its cache read-only (cache-read-only), so its clear-cache removes nothing.");
        }

        if (WhyNotCleared(settings) is { } declined)
        {
            return EmptyPlan(declined);
        }

        var cache = settings.Cache.Folder!;

        if (NothingToPlanFor(cache, $"Composer is installed but has cached nothing yet ({cache} does not exist).", out var cacheIsLink) is { } nothing)
        {
            return nothing;
        }

        switch (WhyNotEmptied(cache, cacheIsLink))
        {
            case { Unlisted: true }:
                return UnreadableRootPlan(cache);
            case { Why: { } why }:
                return EmptyPlan(why);
        }

        var measured = await MeasureAllAsync([cache], ct).ConfigureAwait(false);

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information,
                $"Composer reports its cache folder as {cache}. Its clear-cache empties it and keeps the folder."),
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
                new RunCommandStep(
                    composer,
                    ComposerSettings.ClearArguments(WorkingDirectory),
                    "Clear the Composer cache using Composer's own command")
                {
                    // vcs holds git mirrors, and nothing found shows that Composer notices a mirror cut
                    // off part-way through its removal. A clear takes seconds, so it is left to finish.
                    WhenCancelled = CancelledCommand.Finishes,
                    Estimated = measured.Total,
                    MeasuredPaths = [cache],
                },
            ],
            ProtectedPaths = await BuildProtectedPathsAsync(settings, cache, ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>The folder every tool starts in, named to Composer as well. See <see cref="ComposerSettings.AskAsync"/>.</summary>
    private string WorkingDirectory => ProcessRunner.WorkingDirectoryFor(Environment);

    /// <summary>
    /// Why clear-cache must not be run with these settings, as a sentence, or null where it may.
    /// Asked of what the settings name, before anything on the disk.
    /// </summary>
    private string? WhyNotCleared(ComposerSettings settings)
    {
        if (settings.Cache is not { Folder: { } cache, Disabled: false })
        {
            return $"Composer is set to keep its cache in '{settings.Cache.Reported}', which is not a full path, "
                + "so Deguffer cannot tell which folder its clear-cache would empty.";
        }

        if (ConfiguredFolder.WhyNotOwned(
                cache, Environment, _system, Volumes, TempRoots.Resolve(Environment, _system, Volumes).AccountFolders) is { } notOwned)
        {
            return $"Composer is set to keep its cache in '{cache}', and Deguffer will not run its clear-cache there: "
                + $"{notOwned} It is leaving it alone.";
        }

        // Only COMPOSER_HOME set puts the cache inside the home, which is sound. The reverse empties
        // auth.json along with the cache.
        var folder = Reach(cache);

        if (new[] { settings.Home.Folder, settings.DataDirectory.Folder }.OfType<string>().Any(home => folder.Holds(Reach(home))))
        {
            return $"Composer is set to keep its cache in '{cache}', which holds Composer's home, and its clear-cache "
                + "would empty that too, credentials and all. Deguffer is leaving it alone.";
        }

        foreach (var (setting, child, value) in settings.CacheParts)
        {
            if (!value.Disabled
                && (value.Folder is not { } part || !Reach(part).IsSameAs(Reach(Path.Combine(cache, child)))))
            {
                return $"Composer is set to keep part of its cache ({setting}) in '{value.Reported}', outside its cache "
                    + "folder, and its clear-cache empties that folder whatever else it holds. Deguffer cannot tell "
                    + "what else is there, so it is leaving the cache alone.";
            }
        }

        return null;
    }

    /// <summary>
    /// Why the cache folder as it stands on the disk must not be emptied, or nothing where it may.
    /// clear-cache follows a link to empty what it points at, and empties every entry it finds, so a
    /// link or an entry Composer did not make would be removed through a rule that cannot name it.
    /// </summary>
    /// <param name="isLink">Whether the cache is a link, from the probe that found it there.</param>
    private static (bool Unlisted, string? Why) WhyNotEmptied(string cache, bool isLink)
    {
        if (isLink)
        {
            return (false, $"Leaving '{cache}' alone: it is a link to somewhere else, and Composer's clear-cache would "
                + "empty the folder it points at.");
        }

        if (FolderEntries.Of(cache) is not { } entries)
        {
            return (true, null);
        }

        foreach (var entry in entries)
        {
            var recognised = entry is DirectoryInfo
                ? CacheParts.IsDisposable(entry.Name) && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                : entry.Name.Equals(HtaccessName, StringComparison.OrdinalIgnoreCase);

            if (!recognised)
            {
                return (false, $"Composer's cache folder {cache} holds '{entry.Name}', which Composer does not make, and "
                    + "its clear-cache would remove it with everything else. Deguffer is leaving the cache alone.");
            }
        }

        return (false, null);
    }

    /// <summary>
    /// §5.6. clear-cache keeps the cache folder, and the home beside it carries what matters most:
    /// <c>auth.json</c> with the credentials Composer signs in with, and <c>config.json</c>.
    /// </summary>
    private Task<IReadOnlyList<ProtectedPath>> BuildProtectedPathsAsync(
        ComposerSettings settings,
        string cache,
        CancellationToken ct)
    {
        List<(string Path, string Reason)> paths =
        [
            (cache, "Composer's clear-cache empties its cache folder and keeps the folder."),
        ];

        if (settings.Home.Folder is { } home)
        {
            paths.Add((home, "Composer's home, with its settings and globally installed packages."));
            paths.Add((Path.Combine(home, "auth.json"), "The credentials Composer signs in to package repositories with."));
            paths.Add((Path.Combine(home, "config.json"), "Composer's global settings."));
        }

        if (settings.DataDirectory.Folder is { } data
            && !string.Equals(data, settings.Home.Folder, StringComparison.OrdinalIgnoreCase))
        {
            paths.Add((data, "Composer's data folder, which is not cache."));
        }

        return ProtectAsync([.. paths], ct);
    }

    private Task<ComposerSettings?> ResolveSettingsAsync(string composer, CancellationToken ct) =>
        _settings.Get(() => ComposerSettings.AskAsync(Runner, composer, WorkingDirectory, ct));
}
