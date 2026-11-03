using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// ccache's compiler cache, which keeps the output of every C and C++ compile it has seen and is
/// bounded by a size cap (5 GiB unless <c>max_size</c> says otherwise) rather than by age, so a machine
/// that has filled it stays at the cap. Researched from the ccache manual rather than measured, because
/// no ccache was installed on the machine this was written against.
///
/// <para><b>§5.1, and for a reason beyond preference.</b> <c>ccache.conf</c> can sit inside the cache
/// directory: it always does where <c>CCACHE_DIR</c> moves the cache, and it does at the default where
/// the user put it there. <c>ccache --clear</c> removes every cached file and keeps that configuration,
/// while deleting the directory would take the user's size cap, compiler settings and hashing rules with
/// it. That is the difference between an eviction and a reset.</para>
///
/// <para><b>ccache is asked where its cache is, and nothing is cleared where it does not say.</b>
/// <c>CCACHE_DIR</c>, <c>cache_dir</c> in either configuration file, and a legacy
/// <c>%USERPROFILE%\.ccache</c> that takes precedence whenever it exists all move it, and
/// <c>ccache --clear</c> empties whichever folder those settings name. So
/// <c>ccache --get-config cache_dir</c> is the only answer the plan acts on. A guess from the documented
/// rules would vet and protect one folder while the clear emptied another, which is what a ccache older
/// than 4.0, with no <c>--get-config</c> and <c>cache_dir</c> set, would do.</para>
/// </summary>
public sealed class CcacheProvider : CleanupProviderBase
{
    /// <summary>Set by the user to move the cache, and with it the configuration ccache reads.</summary>
    public const string CacheVariable = "CCACHE_DIR";

    /// <summary>The configuration file ccache keeps beside its cache, which its clear keeps.</summary>
    public const string ConfigurationFile = "ccache.conf";

    /// <summary>
    /// What Explore may remove from a cache folder on its own: the sixteen folders ccache shards its
    /// entries into by the first character of their hash. Not <c>tmp</c> or <c>lock</c>, which a running
    /// compile writes in, and never the configuration.
    /// </summary>
    public static readonly DisposableChildSet Shards = new(
    [
        .. "0123456789abcdef".Select(shard => new ChildClassification(
            shard.ToString(),
            SafetyTier.RegenerableCache,
            "Compiled output ccache stored under the first character of each entry's hash. ccache compiles "
            + "it again when a build needs it.")),
    ]);

    private const string ToolRootReason =
        "This is ccache's compiler cache. Deguffer clears it with ccache's own command, which keeps the "
        + "configuration that can sit inside it: the size limit, compiler settings and hashing rules.";

    private const string DeclinedRootReason =
        "ccache is set to keep its cache here, so it keeps its configuration here too.";

    private readonly ISystemDirectories _system;
    private readonly PassMemo<Task<CacheLocation?>> _location = new();

    public CcacheProvider(
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

    public override string Id => "ccache";

    public override string Name => "ccache compiler cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next build that compiles through ccache compiles everything again, so it takes longer once. "
        + "ccache's settings, including its size limit, are kept, and your projects are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "ccache, a compiler cache for C and C++",
        Publisher = "the ccache project",
        Purpose = "ccache keeps the output of every compile it has seen, so a file that has not changed "
            + "is never compiled twice. It fills its cache up to a size limit, 5 GiB unless it is set "
            + "otherwise, and then stays at that size.",
        Recommendation = "Deguffer runs ccache's own clear rather than deleting the folder, because ccache "
            + "can keep its settings inside the cache folder, and its clear keeps them.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["ccache"];

    /// <summary>Where ccache keeps its cache by default, where the legacy folder does not exist.</summary>
    public string DefaultRoot => Path.Combine(Environment.LocalAppData, "ccache");

    /// <summary>The folder older ccache versions used, which still wins wherever it exists.</summary>
    public string LegacyRoot => Path.Combine(Environment.UserProfile, ".ccache");

    /// <summary>
    /// The documented defaults and the folder <see cref="CacheVariable"/> names, where this examines it as
    /// ccache's. The folder ccache reports is declared in <see cref="DiscoverToolRootsAsync"/>, because
    /// only a subprocess can say.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. new[] { FromVariable().Folder, LegacyRoot, DefaultRoot }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => ToolRoot.Of(root, ToolRootReason, Shards)),
    ];

    /// <summary>
    /// The cache ccache reports, and a folder ccache is set to use that this declines to examine as its
    /// own, where only the configuration file is refused. See <see cref="ToolRoot.Sparing"/>.
    /// </summary>
    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        List<ToolRoot> roots = [];

        if (FromVariable().DeclinedFolder is { } declined)
        {
            roots.Add(ToolRoot.Sparing(declined, DeclinedRootReason, [ConfigurationFile]));
        }

        // An answer that names no full path names no folder to declare.
        if (Environment.FindExecutable("ccache") is { } ccache
            && await ResolveLocationAsync(ccache, ct).ConfigureAwait(false) is { Folder: { } reported } location)
        {
            roots.Add(location.Declined is null
                ? ToolRoot.Of(reported, ToolRootReason, Shards)
                : ToolRoot.Sparing(reported, DeclinedRootReason, [ConfigurationFile]));
        }

        return roots;
    }

    /// <summary>
    /// Every cache <c>ccache --clear</c> can be sent to: the one ccache reports and the documented ones,
    /// because the variable and the legacy folder can change which is in use after a duplicate search has
    /// asked. Never a folder this declines, where the clear is never run.
    /// </summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        List<string> caches = [LegacyRoot, DefaultRoot];

        if (FromVariable().Folder is { } named)
        {
            caches.Add(named);
        }

        if (Environment.FindExecutable("ccache") is { } ccache
            && await ResolveLocationAsync(ccache, ct).ConfigureAwait(false) is { Folder: { } reported, Declined: null })
        {
            caches.Add(reported);
        }

        return [.. caches.Distinct(StringComparer.OrdinalIgnoreCase).Select(CleanedPlace.Whole)];
    }

    /// <summary>
    /// ccache on the PATH, because its own clear is the only route this takes. A cache whose program is
    /// gone is not offered: nothing here would empty it without deleting the configuration beside it.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("ccache") is not null);

    /// <summary>
    /// Every answer here is configuration, which the user can change between one scan and the next.
    /// </summary>
    public override void InvalidateCaches()
    {
        _location.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (Environment.FindExecutable("ccache") is not { } ccache)
        {
            return EmptyPlan("ccache is not installed on this machine.");
        }

        var location = await ResolveLocationAsync(ccache, ct).ConfigureAwait(false);

        if (location is null)
        {
            return EmptyPlan(
                "ccache did not say where its cache is, and its clear empties whichever folder ccache is set "
                + "to use, so Deguffer cannot tell what it would remove. ccache 4.0 and later say.");
        }

        // ccache's clear is sent wherever ccache is set to keep its cache, and a cache set to the profile
        // or a drive root would have it clearing folders that are not ccache's to clear.
        if (location is not { Folder: { } cache, Declined: null })
        {
            return EmptyPlan(
                $"ccache is set to keep its cache in '{location.Reported}', and Deguffer will not run ccache's "
                + $"clear there: {location.Declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(cache, $"ccache is installed but has cached nothing yet ({cache} does not exist).") is { } nothing)
        {
            return nothing;
        }

        var measured = await MeasureAllAsync([cache], ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>
        {
            new RunCommandStep(ccache, "--clear", "Clear the ccache cache using ccache's own command")
            {
                // Each cache entry is one file ccache checks when it reads it, so a clear ended part-way
                // leaves a smaller cache rather than a damaged one.
                WhenCancelled = CancelledCommand.Ended,
                Estimated = measured.Total,
                MeasuredPaths = [cache],
            },
        };

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information, $"ccache reports its cache directory as {cache}."),
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
            Steps = steps,
            ProtectedPaths = Protect(
                (cache, "ccache's clear empties its cache and keeps the folder."),
                (Path.Combine(cache, ConfigurationFile),
                    "ccache's configuration for this cache: its size limit, compiler settings and hashing rules. "
                    + "ccache's clear keeps it.")),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    private Task<CacheLocation?> ResolveLocationAsync(string ccache, CancellationToken ct) =>
        _location.Get(() => AskLocationAsync(ccache, ct));

    /// <summary>
    /// Where ccache says its cache is, vetted because ccache's clear is sent there, or null where ccache
    /// said nothing: a ccache older than 4.0 has no <c>--get-config</c>.
    ///
    /// <para>An answer that is not a full Windows path is declined rather than read as no answer. An
    /// MSYS2 or Cygwin ccache reports <c>/c/Users/...</c>, and its clear empties that folder all the
    /// same.</para>
    /// </summary>
    private async Task<CacheLocation?> AskLocationAsync(string ccache, CancellationToken ct)
    {
        var outcome = await Runner.RunAsync(ccache, "--get-config cache_dir", ToolRun.Query, ct).ConfigureAwait(false);

        var answer = outcome.LastLine;

        if (answer is null)
        {
            return null;
        }

        if (LongPath.Configured(answer) is not { } folder)
        {
            return new CacheLocation(
                answer, Folder: null, "it is not a full path, so Deguffer cannot tell which folder it means.");
        }

        return new CacheLocation(
            answer,
            folder,
            ConfiguredFolder.WhyNotOwned(
                folder,
                Environment,
                _system,
                Volumes,
                TempRoots.Resolve(Environment, _system, Volumes).AccountFolders));
    }

    /// <summary>
    /// The documented rules, for the declarations a scan makes without asking ccache. Both defaults are
    /// declared whichever is in use, so the legacy folder's precedence decides nothing here.
    /// </summary>
    private ConfiguredFolder.Setting FromVariable() =>
        ConfiguredFolder.FromVariable(CacheVariable, DefaultRoot, Environment, _system, Volumes);

    /// <param name="Reported">What ccache printed, for the plan to quote.</param>
    /// <param name="Folder">The full path it names, or null where it names none.</param>
    /// <param name="Declined">
    /// Why the clear must not be sent there, as the end of a sentence, or null where it may be.
    /// </param>
    private sealed record CacheLocation(string Reported, string? Folder, string? Declined);
}
