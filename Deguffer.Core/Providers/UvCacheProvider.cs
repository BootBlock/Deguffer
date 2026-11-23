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
    /// <summary>uv's answers as the memory gives them, for Explore and the duplicate search.</summary>
    private readonly PassMemo<Task<UvStorage>> _storage = new();

    /// <summary>uv's answers asked of uv now, for the plan.</summary>
    private readonly PassMemo<Task<UvStorage>> _askedStorage = new();

    private readonly ToolAnswerMemory _memory;

    /// <param name="environment">The profile, the PATH and the variables uv is started with.</param>
    /// <param name="runner">Starts uv.</param>
    /// <param name="inspector">Tells whether uv is running.</param>
    /// <param name="scanner">Measures the cache.</param>
    /// <param name="memory">
    /// Where uv's answers about its folders are kept while nothing it read has changed: the memory for
    /// <paramref name="runner"/> when null, which is the process-wide one for the real runner.
    /// </param>
    public UvCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ToolAnswerMemory? memory = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
        _memory = memory ?? ToolAnswerMemory.For(runner);
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
    /// uv's two profile folders, the two roots above. The folders uv reports are declared only by
    /// <see cref="DiscoverToolRootsAsync"/>, which this scope does not bound.
    /// </summary>
    public override ToolRootScope ToolRootScope => ToolRootScope.Under(LocalRoot, RoamingRoot);

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
    /// <c>UV_PYTHON_INSTALL_DIR</c> can move between one scan and the next, so the pass's answers are
    /// forgotten. The memory behind them keeps uv's answers only while everything uv read to give them
    /// (<see cref="UvInputs"/>) reads the same, and the plan asks uv afresh regardless.
    /// </summary>
    public override void InvalidateCaches()
    {
        _storage.Forget();
        _askedStorage.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// uv's answers for where its cache, tools and interpreters are, asked as the pass begins: the plan
    /// cannot check the cache against the other two until all three uv processes have answered.
    /// </summary>
    public override Task BeginPass(PassStart start) =>
        start.Ask(ct => Environment.FindExecutable("uv") is { } uv ? AskStorageAfreshAsync(uv, ct) : Task.CompletedTask);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var uv = Environment.FindExecutable("uv");
        if (uv is null)
        {
            return EmptyPlan("uv is not installed on this machine.");
        }

        var storage = await AskStorageAfreshAsync(uv, ct).ConfigureAwait(false);
        var located = storage.Cache;
        var cacheDirectory = located.Path;

        if (storage.CacheConflict() is { } conflict)
        {
            return EmptyPlan(UvStorage.DescribeConflict(cacheDirectory, conflict));
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

    /// <summary>
    /// uv's answers as the memory holds them while nothing uv read has changed, for what Explore refuses
    /// and what a duplicate search leaves to uv.
    /// </summary>
    private Task<UvStorage> ResolveStorageAsync(string uv, CancellationToken ct) =>
        _storage.Get(() => AskStorageAsync(uv, _memory.AskAsync, ct));

    /// <summary>
    /// uv's answers asked of uv now, once a pass, and then remembered: for the plan, whose check of the
    /// cache against uv's own folders (<see cref="UvStorage.CacheConflict"/>) is what keeps
    /// <c>uv cache clean</c>, which removes the whole cache folder, from taking the tools and
    /// interpreters with it. An input the memory missed would let a moved cache past that check (§5.2)
    /// rather than misstate a figure.
    /// </summary>
    private Task<UvStorage> AskStorageAfreshAsync(string uv, CancellationToken ct) =>
        _askedStorage.Get(() => AskStorageAsync(uv, _memory.AskAfreshAsync, ct));

    /// <summary>
    /// The three folders uv can report, asked at once, and the documented layout behind each. The
    /// legacy state folder is probed only to choose the default, so a refusal keeps both candidates.
    /// </summary>
    /// <param name="ask">How each folder is asked: the memory's answer where it still holds, or uv now.</param>
    private async Task<UvStorage> AskStorageAsync(
        string uv,
        Func<ToolQuestion<AskedLocation>, CancellationToken, Task<AskedLocation>> ask,
        CancellationToken ct)
    {
        // One reading for the block the answers are compared by and the variables naming uv's
        // configuration, so the two can never describe different moments.
        var variables = Environment.ToolEnvironment;
        var inputs = UvInputs.Of(uv, Environment, variables);
        var stateRoots = UvStorage.StateRootsFor(
            RoamingRoot, LongPath.ProbeDirectory(UvStorage.LegacyStateRoot(RoamingRoot)));
        var cache = AskOneAsync("cache dir", DefaultCacheDirectory);
        var tools = AskOneAsync("tool dir", Path.Combine(stateRoots[0], "tools"));
        var python = AskOneAsync("python dir", Path.Combine(stateRoots[0], "python"));

        await Task.WhenAll(cache, tools, python).ConfigureAwait(false);

        return new UvStorage(await cache, await tools, await python, stateRoots, LocalRoot, RoamingRoot);

        // One question per folder, so a uv that answers one and not another still has the ones it gave
        // kept. The default a failed query falls back to is never kept.
        Task<AskedLocation> AskOneAsync(string query, string fallback) => ask(
            new ToolQuestion<AskedLocation>(
                $"{query} --color never|{uv}",
                variables,
                _ => inputs,
                static located => located.Answered,
                askCt => QueryAsync(uv, query, fallback, askCt)),
            ct);
    }

    private async Task<AskedLocation> QueryAsync(string uv, string query, string fallback, CancellationToken ct)
    {
        // --color never because uv colourises a path when it thinks it is writing to a terminal, and
        // the escape sequences would land inside the parsed path.
        var outcome = await Runner.RunAsync(uv, $"{query} --color never", ToolRun.Query, ct).ConfigureAwait(false);
        var printed = AskedLocation.FromLastRootedLine(outcome, fallback);

        // Resolved as configuration, because uv can print a cache-dir as it was typed: forward slashes
        // or a '..' would compare equal to none of uv's folders and walk past the overlap check.
        return AskedLocation.Of(printed.Answered ? LongPath.Configured(printed.Path) : null, fallback);
    }
}
