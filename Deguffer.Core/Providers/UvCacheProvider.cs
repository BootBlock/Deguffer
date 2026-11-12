using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The uv (Python) package cache (~3.5 GB on the audited machine).
///
/// §5.1: uv has an official eviction command, so the plan calls it rather than deleting the cache
/// directory. The tools and managed interpreters uv installs are in another profile entirely, under
/// <c>%APPDATA%\uv</c> beside uv's credential store and configuration, and <c>cache-dir</c> can point
/// the cache anywhere, so uv is asked where each of them is. <see cref="UvStorage"/> holds the layout.
/// </summary>
public sealed class UvCacheProvider : CleanupProviderBase
{
    private readonly PassMemo<Task<UvStorage>> _storage = new();

    public UvCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
    }

    public override string Id => "uv";

    public override string Name => "uv package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next uv install re-downloads and re-unpacks packages. Existing virtual environments, " +
        "the tools installed with 'uv tool install' and the Python interpreters uv manages are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "uv, a Python package and project manager",
        Publisher = "Astral",
        Purpose = "uv caches the archives it downloads and the unpacked wheels it builds "
            + "environments from, so creating another environment with the same dependencies costs "
            + "almost nothing.",
        Recommendation = "Deguffer runs uv's own cache clean rather than deleting a path, so only the "
            + "cache goes. The tools and Python interpreters uv installed, and the passwords it saved, "
            + "are kept elsewhere and must not go with it.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["uv", "uvx"];

    /// <summary>
    /// Where uv keeps its cache when it has not been asked. The location moves with
    /// <c>UV_CACHE_DIR</c>, <c>--cache-dir</c> and <c>cache-dir</c> in <c>uv.toml</c>, so this is a
    /// last resort rather than an assumption.
    /// </summary>
    public string DefaultCacheDirectory => Path.Combine(Environment.LocalAppData, "uv", "cache");

    /// <summary>uv's folder in the local profile, which holds the cache (§5.2).</summary>
    public string LocalRoot => Path.Combine(Environment.LocalAppData, "uv");

    /// <summary>
    /// uv's folder in the roaming profile, which holds its configuration, and by default its tools,
    /// interpreters and credentials. Nothing in it is a cache.
    /// </summary>
    public string RoamingRoot => Path.Combine(Environment.RoamingAppData, "uv");

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: the cache is the one child of the local folder that
    /// may go, and nothing in the roaming folder may.
    ///
    /// <para>The folders uv reports are declared in <see cref="DiscoverToolRootsAsync"/>, because only
    /// a subprocess can say where they are.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        ToolRoot.Folders(
            LocalRoot,
            "This is uv's own folder. Deguffer clears the cache inside it and nothing else.",
            static name => name.Equals("cache", StringComparison.OrdinalIgnoreCase)),
        new ToolRoot(
            RoamingRoot,
            "This is uv's own folder. It holds the tools you installed with 'uv tool install', the Python "
            + "interpreters uv manages, the passwords and tokens uv saved, and its settings. Removing it "
            + "would uninstall them and break every environment built on one of those interpreters.",
            static _ => false),
    ];

    /// <summary>
    /// The tool and interpreter folders uv reports, where <c>UV_TOOL_DIR</c> or
    /// <c>UV_PYTHON_INSTALL_DIR</c> can have moved them out of <see cref="RoamingRoot"/>.
    /// </summary>
    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        if (Environment.FindExecutable("uv") is not { } uv)
        {
            return [];
        }

        var storage = await ResolveStorageAsync(uv, ct).ConfigureAwait(false);

        return
        [
            .. new[]
                {
                    (storage.Tools, "uv reports this as where it keeps the tools you installed with 'uv tool install'."),
                    (storage.Python, "uv reports this as where it keeps the Python interpreters it manages."),
                }
                .Where(reported => reported.Item1.Answered)
                .Select(reported => new ToolRoot(reported.Item1.Path, reported.Item2, static _ => false)),
        ];
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("uv") is not null);

    /// <summary>
    /// The cache <c>uv cache clean</c> is sent to: the one uv reports, and the documented default as
    /// well, because <c>UV_CACHE_DIR</c> can move the cache back to it after a duplicate search has
    /// asked. Never a reported cache the plan declines because it overlaps uv's own folders, where the
    /// clean is never run.
    /// </summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        var uv = Environment.FindExecutable("uv");

        if (uv is null)
        {
            return [CleanedPlace.Whole(DefaultCacheDirectory)];
        }

        var storage = await ResolveStorageAsync(uv, ct).ConfigureAwait(false);

        return storage.CacheConflict() is null
            ? [CleanedPlace.Whole(DefaultCacheDirectory), CleanedPlace.Whole(storage.Cache.Path)]
            : [CleanedPlace.Whole(DefaultCacheDirectory)];
    }

    /// <summary>
    /// Every folder uv reports is configuration, which <c>UV_CACHE_DIR</c>, <c>UV_TOOL_DIR</c> and
    /// <c>UV_PYTHON_INSTALL_DIR</c> can move between one scan and the next. Keeping them across an
    /// invalidation would measure and protect locations uv has stopped using.
    /// </summary>
    public override void InvalidateCaches()
    {
        _storage.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var uv = Environment.FindExecutable("uv");
        if (uv is null)
        {
            return EmptyPlan("uv is not installed on this machine.");
        }

        var storage = await ResolveStorageAsync(uv, ct).ConfigureAwait(false);
        var located = storage.Cache;
        var cacheDirectory = located.Path;

        if (storage.CacheConflict() is { } conflict)
        {
            return EmptyPlan(
                $"uv's cache directory is set to {cacheDirectory}, which overlaps {conflict}. uv's own clean "
                + "removes the whole cache directory, so it would take uv's tools, interpreters, credentials "
                + "or settings with it. Deguffer offers nothing until the cache is moved to a folder of its own.");
        }

        if (NothingToPlanFor(cacheDirectory, located.DescribeAbsent("uv", "cache directory")) is { } nothing)
        {
            return nothing;
        }

        var measured = await MeasureAllAsync([cacheDirectory], ct).ConfigureAwait(false);
        var notes = new List<PlanNote>();

        // Deliberately not --force: that flag tells uv to ignore its own in-use checks, and §5.3
        // prefers warning the user that something is running over overriding the tool's judgement.
        var steps = new List<CleanupStep>
        {
            new RunCommandStep(uv, "cache clean", "Clear the uv cache using uv's own command")
            {
                // uv's cache links its entries to one another, and nothing shows a clean ended part-way leaves
                // only whole entries behind. Left to finish.
                WhenCancelled = CancelledCommand.Finishes,
                Estimated = measured.Total,
                MeasuredPaths = [cacheDirectory],
            },
        };

        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            located.Describe("uv", "cache directory")));

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
            Steps = steps,
            ProtectedPaths = await ProtectAsync(storage.Survivors, ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    private Task<UvStorage> ResolveStorageAsync(string uv, CancellationToken ct) =>
        _storage.Get(() => AskStorageAsync(uv, ct));

    /// <summary>
    /// The three folders uv can report, asked at once, and the documented layout behind each. The
    /// legacy state folder is probed only to choose the default, so a refusal keeps both candidates.
    /// </summary>
    private async Task<UvStorage> AskStorageAsync(string uv, CancellationToken ct)
    {
        var stateRoots = UvStorage.StateRootsFor(
            RoamingRoot, LongPath.ProbeDirectory(UvStorage.LegacyStateRoot(RoamingRoot)));
        var cache = AskAsync(uv, "cache dir", DefaultCacheDirectory, ct);
        var tools = AskAsync(uv, "tool dir", Path.Combine(stateRoots[0], "tools"), ct);
        var python = AskAsync(uv, "python dir", Path.Combine(stateRoots[0], "python"), ct);

        await Task.WhenAll(cache, tools, python).ConfigureAwait(false);

        return new UvStorage(await cache, await tools, await python, stateRoots, LocalRoot, RoamingRoot);
    }

    private async Task<AskedLocation> AskAsync(string uv, string query, string fallback, CancellationToken ct)
    {
        // --color never because uv colourises a path when it thinks it is writing to a terminal, and
        // the escape sequences would land inside the parsed path.
        var outcome = await Runner.RunAsync(uv, $"{query} --color never", ToolRun.Query, ct).ConfigureAwait(false);

        return AskedLocation.FromLastRootedLine(outcome, fallback);
    }
}
