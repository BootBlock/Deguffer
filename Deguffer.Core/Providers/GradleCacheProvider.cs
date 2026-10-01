using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Gradle build caches and wrapper distributions (~7 GB on the audited machine).
///
/// Gradle offers no official cache-eviction command, so this is the path-based case — which is
/// exactly where §5.2 bites: <c>gradle.properties</c> sits alongside the caches and may contain
/// signing keys and credentials. Only <c>caches</c> and <c>wrapper</c> are ever targeted, and the
/// Gradle user home is never a target itself.
///
/// <para>The home is wherever <see cref="HomeVariable"/> points, and <c>.gradle</c> in the profile
/// only where it is unset. Developers set it to move a cache of several gigabytes off the system
/// drive, and a row that read only the default would miss that cache and leave the configuration
/// beside it undeclared to Explore.</para>
/// </summary>
public sealed class GradleCacheProvider : CleanupProviderBase
{
    /// <summary>Set by the user to move the whole Gradle user home, caches and configuration together.</summary>
    public const string HomeVariable = "GRADLE_USER_HOME";

    /// <summary>
    /// The only children of the Gradle user home this provider recognises. Anything else is Tier 4 by
    /// construction — see <see cref="DisposableChildSet"/>.
    /// </summary>
    public static readonly DisposableChildSet DisposableChildren = new(
    [
        new ChildClassification(
            "caches",
            SafetyTier.RegenerableCache,
            "Dependency and build caches. Gradle re-downloads and re-derives them on the next build."),
        new ChildClassification(
            "wrapper",
            SafetyTier.RegenerableCache,
            "Downloaded Gradle distributions. The wrapper re-fetches the version a project asks for."),
    ]);

    private static readonly IReadOnlyList<CacheLevel> Levels = [new CacheLevel(string.Empty, DisposableChildren)];

    private const string ToolRootReason =
        "This is Gradle's own folder. Deguffer removes the caches and wrapper distributions inside it "
        + "and nothing else, because the configuration beside them may hold signing keys and credentials.";

    private readonly ISystemDirectories _system;

    public GradleCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "gradle";

    public override string Name => "Gradle build cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next Gradle build re-downloads its dependencies and the wrapper distribution, then runs normally.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Gradle, the build tool behind Android and many Java and Kotlin projects",
        Publisher = "Gradle Inc.",
        Purpose = "Gradle keeps the dependencies it downloads, the Gradle distributions each "
            + "project's wrapper pins, and the outputs of tasks it has already run, all under one "
            + "folder that every project shares: .gradle in your profile, unless GRADLE_USER_HOME "
            + "moves it.",
        Recommendation = "The next build re-downloads what it needs and re-runs what it cannot "
            + "find, so the cost is one slower build. Deguffer targets the disposable folders "
            + "inside that folder and never the folder itself, which also holds gradle.properties.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["java", "gradle", "studio64"];

    /// <summary>Where Gradle keeps its user home when <see cref="HomeVariable"/> is unset.</summary>
    public string DefaultHome => Path.Combine(Environment.UserProfile, ".gradle");

    /// <summary>
    /// The Gradle user home, honouring <see cref="HomeVariable"/>, or null where that names nothing
    /// this can examine as Gradle's. Gradle resolves a relative value against the working directory
    /// of the build, which Deguffer is not, so there is no correct reading of one. And a drive root,
    /// one of the account's own folders, or a folder holding a temporary or Windows folder is not
    /// examined as Gradle's whatever the variable says: <c>caches</c> and <c>wrapper</c> are names
    /// anything may use. See <see cref="ConfiguredFolder"/>.
    /// </summary>
    public string? ResolveHome() => Resolve().Home;

    /// <summary>
    /// The home Gradle uses, and the default one as well where the variable moved it. A
    /// <c>.gradle</c> left behind in the profile still holds whatever <c>gradle.properties</c> was
    /// written before the move, so Explore refuses it there exactly as it does in the home in use. A
    /// home the variable names but this declines is not declared: declaring it would let Explore
    /// remove whatever that folder holds called <c>caches</c> or <c>wrapper</c>.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. new[] { ResolveHome(), DefaultHome }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(home => ToolRoot.Of(home, ToolRootReason, DisposableChildren)),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveHome() is { } home && LongPath.DirectoryMayExist(home));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var setting = Resolve();

        if (setting.Home is not { } home)
        {
            return EmptyPlan(
                $"{HomeVariable} is set to '{setting.Value}', and Deguffer will not treat that as Gradle's "
                + $"folder: {setting.Declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(
                home,
                $"Gradle is not installed for this user — no {home} directory.") is { } nothing)
        {
            return nothing;
        }

        // Moving .gradle onto another drive with a junction is common. The walk below would decline
        // it too, but this says so about the whole folder in one sentence, and makes no claim about
        // a gradle.properties that only resolves through the link.
        if (LongPath.IsReparsePoint(home))
        {
            return UnexaminedPlan(
                $"Leaving '{home}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var walk = CacheLevelWalk.Under(Levels, home, ct);

        List<PlanNote> notes = [.. walk.Notes, .. walk.Survivors.Select(CacheLevelWalk.SparedNote)];

        var (steps, measured) = await PlanDeletionsAsync(walk.Targets, keep, ct).ConfigureAwait(false);

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
            ProtectedPaths = BuildProtectedPaths(home, walk),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = walk.Unreadable,
            WasNotExamined = walk.Targets.Count == 0 && walk.Declined.Count > 0,
        };
    }

    /// <summary>
    /// §5.6. The home itself and the config beside it are the whole reason this provider is
    /// path-based rather than a recursive delete, so they are what the run has to prove. Every child
    /// the walk spared or declined is named as well: <c>jdks</c>, <c>native</c> and <c>daemon</c> are
    /// siblings of the two targets, which is exactly where an over-broad rule takes one with the other.
    /// </summary>
    private static IReadOnlyList<ProtectedPath> BuildProtectedPaths(string home, LevelWalk walk) => Protect(
        walk,
        (home, "The Gradle user home itself must survive — only its known-disposable children are removed."),
        (Path.Combine(home, "gradle.properties"), "User configuration, which may hold signing keys and credentials."),
        (Path.Combine(home, "init.d"), "User init scripts."),
        (Path.Combine(home, "gradle.encrypted.properties"), "Encrypted user configuration."));

    private GradleHomeSetting Resolve()
    {
        var value = Environment.GetEnvironmentVariable(HomeVariable)?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return new GradleHomeSetting(null, DefaultHome, null);
        }

        if (LongPath.Configured(value) is not { } configured)
        {
            return new GradleHomeSetting(value, null, "it is not a full path, so Deguffer cannot tell which folder it means.");
        }

        return ConfiguredFolder.WhyNotOwned(configured, Environment, _system, TempRoots.Resolve(Environment, _system).AccountFolders) is { } declined
            ? new GradleHomeSetting(value, null, declined)
            : new GradleHomeSetting(value, configured, null);
    }

    /// <param name="Value">What <see cref="HomeVariable"/> holds, or null where it is not set.</param>
    /// <param name="Home">The user home to examine, or null where it is declined.</param>
    /// <param name="Declined">Why it is declined, as the end of a sentence, or null where it is not.</param>
    private readonly record struct GradleHomeSetting(string? Value, string? Home, string? Declined);
}
