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
/// <para><b>ccache is asked where its cache is.</b> <c>CCACHE_DIR</c>, <c>cache_dir</c> in either
/// configuration file, and a legacy <c>%USERPROFILE%\.ccache</c> that takes precedence whenever it exists
/// all move it, so <c>ccache --get-config cache_dir</c> is the authority, and the documented rules are
/// the fallback where it does not answer.</para>
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
    private readonly PassMemo<Task<CacheLocation>> _location = new();

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

        // Only an answer ccache gave: the documented rules' answer is the variable or a default, which
        // the declaration above and ToolRoots already make.
        if (Environment.FindExecutable("ccache") is { } ccache
            && await ResolveLocationAsync(ccache, ct).ConfigureAwait(false) is { Answered: true } location)
        {
            roots.Add(location.Declined is null
                ? ToolRoot.Of(location.Folder, ToolRootReason, Shards)
                : ToolRoot.Sparing(location.Folder, DeclinedRootReason, [ConfigurationFile]));
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
            && await ResolveLocationAsync(ccache, ct).ConfigureAwait(false) is { Answered: true, Declined: null } location)
        {
            caches.Add(location.Folder);
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
        var cache = location.Folder;

        // ccache's clear is sent wherever ccache is set to keep its cache, and a cache set to the profile
        // or a drive root would have it clearing folders that are not ccache's to clear.
        if (location.Declined is { } declined)
        {
            return EmptyPlan(
                $"ccache is set to keep its cache in '{cache}', and Deguffer will not run ccache's clear "
                + $"there: {declined} It is leaving it alone.");
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
            new(PlanNoteSeverity.Information, location.Answered
                ? $"ccache reports its cache directory as {cache}."
                : $"ccache did not say where its cache is, so this is the documented location rather than "
                  + $"this machine's setting: {cache}."),
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

    private Task<CacheLocation> ResolveLocationAsync(string ccache, CancellationToken ct) =>
        _location.Get(() => AskLocationAsync(ccache, ct));

    /// <summary>
    /// Ask ccache, and where it does not answer with a full path (a ccache older than 4.0 has no
    /// <c>--get-config</c>), apply the documented rules: the variable, then the legacy folder if it
    /// exists, then the default. Either answer is vetted the same way, because ccache's clear is sent
    /// there whichever said so.
    /// </summary>
    private async Task<CacheLocation> AskLocationAsync(string ccache, CancellationToken ct)
    {
        var outcome = await Runner.RunAsync(ccache, "--get-config cache_dir", ToolRun.Query, ct).ConfigureAwait(false);

        var reported = outcome.Succeeded
            ? outcome.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(LongPath.Configured)
                .LastOrDefault(line => line is not null)
            : null;

        if (reported is null)
        {
            var setting = FromVariable();

            // The variable's own value where it names no full path, because that is what the plan quotes
            // when it declines it.
            return new CacheLocation(setting.Folder ?? setting.Named ?? setting.Value!, setting.Declined, Answered: false);
        }

        return new CacheLocation(
            reported,
            ConfiguredFolder.WhyNotOwned(
                reported,
                Environment,
                _system,
                Volumes,
                TempRoots.Resolve(Environment, _system, Volumes).AccountFolders),
            Answered: true);
    }

    /// <summary>The documented rules, which the legacy folder decides wherever the variable is unset.</summary>
    private ConfiguredFolder.Setting FromVariable() =>
        ConfiguredFolder.FromVariable(
            CacheVariable,
            LongPath.DirectoryMayExist(LegacyRoot) ? LegacyRoot : DefaultRoot,
            Environment,
            _system,
            Volumes);

    /// <param name="Folder">The cache, in the form a plan holds it.</param>
    /// <param name="Declined">
    /// Why the clear must not be sent there, as the end of a sentence, or null where it may be.
    /// </param>
    /// <param name="Answered">Whether ccache itself said so, rather than the documented rules.</param>
    private sealed record CacheLocation(string Folder, string? Declined, bool Answered);
}
