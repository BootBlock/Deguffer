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
/// <c>.gradle</c> root is never a target itself.
/// </summary>
public sealed class GradleCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// The only children of <c>.gradle</c> this provider recognises. Anything else is Tier 4 by
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

    private readonly string _root;

    public GradleCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _root = Path.Combine(Environment.UserProfile, ".gradle");
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
            + "folder in your profile that every project shares.",
        Recommendation = "The next build re-downloads what it needs and re-runs what it cannot "
            + "find, so the cost is one slower build. Deguffer targets the disposable folders "
            + "inside .gradle and never the folder itself, which also holds gradle.properties.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["java", "gradle", "studio64"];

    /// <summary>The <c>.gradle</c> root. Exposed so tests can assert it is never targeted.</summary>
    public string RootPath => _root;

    /// <inheritdoc />
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        ToolRoot.Of(
            _root,
            "This is Gradle's own folder. Deguffer removes the caches and wrapper distributions "
            + "inside it and nothing else, because the configuration beside them may hold signing "
            + "keys and credentials.",
            DisposableChildren),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.DirectoryMayExist(_root));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (NothingToPlanFor(
                _root,
                "Gradle is not installed for this user — no .gradle directory.") is { } nothing)
        {
            return nothing;
        }

        // Moving .gradle onto another drive with a junction is common. The walk below would decline
        // it too, but this says so about the whole folder in one sentence, and makes no claim about
        // a gradle.properties that only resolves through the link.
        if (LongPath.IsReparsePoint(_root))
        {
            return UnexaminedPlan(
                $"Leaving '{_root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var walk = CacheLevelWalk.Under(Levels, _root, ct);

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
            ProtectedPaths = BuildProtectedPaths(walk),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = walk.Unreadable,
            WasNotExamined = walk.Targets.Count == 0 && walk.Declined.Count > 0,
        };
    }

    /// <summary>
    /// §5.6. The root itself and the config beside it are the whole reason this provider is
    /// path-based rather than a recursive delete, so they are what the run has to prove. Every child
    /// the walk spared or declined is named as well: <c>jdks</c>, <c>native</c> and <c>daemon</c> are
    /// siblings of the two targets, which is exactly where an over-broad rule takes one with the other.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(LevelWalk walk) => Protect(
        walk,
        (_root, "The .gradle root itself must survive — only its known-disposable children are removed."),
        (Path.Combine(_root, "gradle.properties"), "User configuration, which may hold signing keys and credentials."),
        (Path.Combine(_root, "init.d"), "User init scripts."),
        (Path.Combine(_root, "gradle.encrypted.properties"), "Encrypted user configuration."));
}
