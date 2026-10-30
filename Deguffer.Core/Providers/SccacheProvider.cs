using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// sccache's local disk cache, which keeps the output of every compile it has seen and is bounded by a
/// size cap (10 GiB unless <c>SCCACHE_CACHE_SIZE</c> says otherwise) rather than by age, so a machine
/// that has filled it stays at the cap. Researched from sccache's source rather than measured, because
/// no sccache was installed on the machine this was written against.
///
/// <para><b>§5.1 was asked and the answer is no.</b> sccache's command line offers
/// <c>--zero-stats</c>, which resets counters and frees nothing, and no command that empties the cache.
/// So this is the path-based case.</para>
///
/// <para><b>The server is stopped first.</b> Compiles go through a background server that keeps the
/// cache's index in memory, and removing entries under it leaves that index counting files that are
/// gone. Each removal's use check is a <see cref="ReleasingProcessCheck"/>, which sends
/// <c>sccache --stop-server</c> once, only while sccache runs, and holds the removal back if it is still
/// running afterwards. A build in progress restarts the server on its next compile, and its cache is then left
/// alone.</para>
///
/// <para><b>The root is unusually clean.</b> sccache's own source says nothing else may use this
/// directory, because it writes its entries straight into it, and it keeps its configuration in a
/// different root entirely (<c>%APPDATA%\Mozilla\sccache\config</c>). Only the names sccache writes are
/// recognised all the same: <c>SCCACHE_DIR</c> can point anywhere, and §5.2 is about what a name proves,
/// not about what a tool promises.</para>
///
/// <para>The default is <c>%LOCALAPPDATA%\Mozilla\sccache\cache</c>: sccache asks the <c>directories</c>
/// crate for its cache folder, which on Windows ends in <c>cache</c> beneath the project's folder.
/// sccache's own documentation names the folder above it.</para>
/// </summary>
public sealed class SccacheProvider : CleanupProviderBase
{
    /// <summary>Set by the user to move the cache.</summary>
    public const string CacheVariable = "SCCACHE_DIR";

    /// <summary>The program that is both the compiler wrapper and its background server.</summary>
    private static readonly IReadOnlyList<string> Server = ["sccache"];

    private const string EntryReason =
        "Compiled output sccache stored under the first character of each entry's hash. sccache "
        + "compiles it again when a build needs it.";

    /// <summary>
    /// What may be deleted: the sixteen folders sccache shards its entries into by the first character of
    /// their hash, and the cache of preprocessor results beside them. Anything not named here is Tier 4
    /// by construction.
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        .. "0123456789abcdef".Select(shard => new ChildClassification(shard.ToString(), SafetyTier.RegenerableCache, EntryReason)),
        new ChildClassification(
            "preprocessor",
            SafetyTier.RegenerableCache,
            "sccache's record of what the preprocessor produced for each source file, which lets it skip "
            + "running the preprocessor again. sccache records it again on the next build."),
    ]);

    private static readonly IReadOnlyList<CacheLevel> Levels = [new CacheLevel(string.Empty, Children)];

    private const string ToolRootReason =
        "This is sccache's compiler cache. Deguffer removes the cached entries inside it, never the "
        + "folder itself.";

    private readonly ISystemDirectories _system;
    private readonly TimeProvider _time;

    /// <param name="time">The clock the clean waits on while the stopped server exits.</param>
    public SccacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null,
        TimeProvider? time = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            time: time,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _time = time ?? TimeProvider.System;
    }

    public override string Id => "sccache";

    public override string Name => "sccache compiler cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next build that compiles through sccache compiles everything again, so it takes longer once. "
        + "If sccache's background server is running, the clean stops it first, and the next compile "
        + "starts it again. Your projects and sccache's settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "sccache, a compiler cache for Rust, C and C++",
        Publisher = "Mozilla",
        Purpose = "sccache keeps the output of every compile it has seen, so a file that has not changed "
            + "is never compiled twice. It fills its cache up to a size limit, 10 GiB unless it is set "
            + "otherwise, and then stays at that size.",
        Recommendation = "sccache has no command that empties its cache, so Deguffer removes the cached "
            + "entries itself, after stopping sccache's background server. sccache keeps its settings in "
            + "a different folder, which Deguffer never touches.",
    };

    /// <summary>Where sccache keeps its cache when <see cref="CacheVariable"/> is unset.</summary>
    public string DefaultRoot => Path.Combine(Environment.LocalAppData, "Mozilla", "sccache", "cache");

    /// <summary>
    /// The cache, honouring <see cref="CacheVariable"/>, or null where that names nothing this can
    /// examine as sccache's. See <see cref="ConfiguredFolder"/>.
    /// </summary>
    public string? ResolveRoot() => Resolve().Folder;

    /// <summary>
    /// The cache in use, and the default one as well where the variable moved it, because a cache left
    /// at the default is still sccache's. A folder the variable names but this declines is not
    /// declared: sccache keeps no configuration there to spare.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. Roots().Select(root => ToolRoot.Of(root, ToolRootReason, Children)),
    ];

    /// <summary>The recognised children of the cache in use and of the default cache.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. Roots().SelectMany(root => Children.DisposableNames
                .Select(child => CleanedPlace.Whole(Path.Combine(root, child)))),
        ]);

    /// <summary>
    /// Presence is a cache on the disk, never sccache on the PATH: a cache outlives the program that
    /// wrote it, and nothing here needs the program unless its server is running.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveRoot() is { } root
            && Children.DisposableNames.Any(name => LongPath.DirectoryMayExist(Path.Combine(root, name))));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var setting = Resolve();

        if (setting.Folder is not { } root)
        {
            return EmptyPlan(
                $"{CacheVariable} is set to '{setting.Value}', and Deguffer will not treat that as sccache's "
                + $"cache: {setting.Declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(root, $"sccache has not cached anything for this user — no {root} directory.") is { } nothing)
        {
            return nothing;
        }

        // The root arrives by name, from an environment variable or a default, so nothing has classified
        // it, and a recognised name on the far side of a link would be removed through it.
        if (LongPath.IsReparsePoint(root))
        {
            return UnexaminedPlan(
                $"Leaving '{root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var walk = CacheLevelWalk.Under(Levels, root, ct);
        var sccache = Environment.FindExecutable("sccache");

        // One check for every removal, so the server is asked to stop once in a clean.
        IUseCheck stillStopped = sccache is null
            ? new RunningProcessCheck(Inspector, Server)
            : new ReleasingProcessCheck(sccache, "--stop-server", Server, Runner, Inspector, _time);

        var (steps, measured) = await PlanDeletionsAsync(
            [.. walk.Targets.Select(target => target with { UseCheck = stillStopped })],
            keep,
            ct).ConfigureAwait(false);

        List<PlanNote> notes = [.. walk.Notes, .. walk.Unrecognised.Select(CacheLevelWalk.SparedNote)];

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (ServerNote(canStop: sccache is not null) is { } server)
        {
            notes.Add(server);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect(
                walk,
                walk.Unrecognised,
                (root, "sccache's cache folder itself must survive — only the cached entries inside it are removed.")),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = walk.Unreadable,
            WasNotExamined = walk.Targets.Count == 0 && walk.Declined.Count > 0,
        };
    }

    /// <summary>
    /// What the clean will do about a server running now. In place of the usual warning, which says what
    /// is held open is left in place: here the clean stops the server, or cannot, and says which.
    /// </summary>
    private PlanNote? ServerNote(bool canStop)
    {
        if (Inspector.FindRunning(Server).Count == 0)
        {
            return null;
        }

        return !canStop
            ? new PlanNote(
                PlanNoteSeverity.Warning,
                "sccache's background server is running, and Deguffer cannot find sccache to stop it, so the "
                + "cache is left alone while it runs. Run 'sccache --stop-server' and clean again.")
            : new PlanNote(
                PlanNoteSeverity.Information,
                "sccache's background server is running. The clean stops it with 'sccache --stop-server' "
                + "first, and the next compile starts it again. A build running at the time starts it again "
                + "at once, and the cache is then left alone.");
    }

    private IEnumerable<string> Roots() =>
        new[] { ResolveRoot(), DefaultRoot }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    private ConfiguredFolder.Setting Resolve() =>
        ConfiguredFolder.FromVariable(CacheVariable, DefaultRoot, Environment, _system, Volumes);
}
