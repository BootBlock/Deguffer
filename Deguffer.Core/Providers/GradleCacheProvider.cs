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
        Task.FromResult(LongPath.DirectoryExists(_root));

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();

        if (!LongPath.DirectoryExists(_root))
        {
            return EmptyPlan("Gradle is not installed for this user — no .gradle directory.");
        }

        // Moving .gradle onto another drive with a junction is common, and the enumeration below
        // never classifies the directory it is handed: it would return the far side's ordinary
        // children, target the recognised ones, and pass every §5.6 assertion, because each survivor
        // named here resolves through the same link.
        if (LongPath.IsReparsePoint(_root))
        {
            return EmptyPlan(
                $"Leaving '{_root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var scan = ChildDirectories.Under(_root);

        // The root was found on disk by name above, and a listing right is separate from a traverse
        // right — so a refusal here leaves a plan with no steps and, without this, nothing said. The
        // shell renders that as "Already clear", which is a claim about a folder nobody read.
        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(_root));
        }

        // A link is a child the user can see, so it is named rather than dropped. It is never
        // followed: what it points at was never classified.
        notes.AddRange(scan.Links.Select(link => new PlanNote(
            PlanNoteSeverity.Information,
            $"Leaving '{link.Name}' alone: it is a link to somewhere else, and Deguffer does not "
            + "delete through a link.")));

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var classification = DisposableChildren.Classify(child.Name);

            if (!classification.Tier.IsOfferable())
            {
                // §5.2: unrecognised means untouched, and the user is told why rather than
                // silently having it omitted.
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{child.Name}' alone: {classification.Reason}"));
                continue;
            }

            // Enumeration runs in extended form; a plan always holds display paths, and I/O
            // re-extends at the point of use. Keeping the prefix out of the plan means it never
            // reaches the UI, a log, or a comparison.
            targets.Add(new DeletionTarget(LongPath.Display(child.FullName), classification.Reason));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, ct).ConfigureAwait(false);

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
            ProtectedPaths = BuildProtectedPaths(),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = scan.Unreadable,
        };
    }

    /// <summary>
    /// §5.6. The root itself and the config beside it are the whole reason this provider is
    /// path-based rather than a recursive delete, so they are what the run has to prove.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths() => Protect(
        (_root, "The .gradle root itself must survive — only its known-disposable children are removed."),
        (Path.Combine(_root, "gradle.properties"), "User configuration, which may hold signing keys and credentials."),
        (Path.Combine(_root, "init.d"), "User init scripts."),
        (Path.Combine(_root, "gradle.encrypted.properties"), "Encrypted user configuration."));
}
