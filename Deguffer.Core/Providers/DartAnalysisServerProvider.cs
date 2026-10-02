using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Dart analysis server's byte store (~3.2 GB on the audited machine), which dart.dev names as
/// <c>%LOCALAPPDATA%\.dartServer</c> in its own performance guidance.
///
/// The server summarises every file it analyses so a later run over the same package can skip the
/// work. Nothing trims that store, so it accumulates one set of summaries per package ever opened
/// and grows out of all proportion to the work in front of it.
///
/// §5.1 has nothing to prefer: the Dart SDK ships no eviction command for this store, and the
/// remedy in its own issue tracker is deleting the directory. So this is the path-based case, like
/// Gradle — and §5.2 bites for the same reason. <c>.prompts</c> holds the user's answers to the
/// server's own prompts, which is a preference rather than a cache, and it sits directly beside the
/// two disposable children.
/// </summary>
public sealed class DartAnalysisServerProvider : CleanupProviderBase
{
    /// <summary>
    /// The only children of <c>.dartServer</c> this provider recognises. Anything else is Tier 4 by
    /// construction — see <see cref="DisposableChildSet"/>.
    /// </summary>
    public static readonly DisposableChildSet DisposableChildren = new(
    [
        new ChildClassification(
            ".analysis-driver",
            SafetyTier.RegenerableCache,
            "Summaries of code the analysis server has already analysed. It re-derives them from "
            + "the sources, which are still on disk."),
        new ChildClassification(
            ".pub-package-details-cache",
            SafetyTier.RegenerableCache,
            "Package details fetched from pub.dev to complete dependency names. The server fetches "
            + "them again when it next needs them."),
    ]);

    private static readonly IReadOnlyList<CacheLevel> Levels = [new CacheLevel(string.Empty, DisposableChildren)];

    private readonly string _root;
    private readonly RowDeclarations _declarations;

    /// <param name="declarations">
    /// What every row in the planner offers, so a child another row removes from a folder this row
    /// walks is not asserted here. See <see cref="RowDeclarations"/>.
    /// </param>
    public DartAnalysisServerProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        RowDeclarations? declarations = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _root = Path.Combine(Environment.LocalAppData, ".dartServer");
        _declarations = declarations ?? new RowDeclarations();
    }

    public override string Id => "dart-analysis-server";

    public override string Name => "Dart analysis server cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next time a Dart or Flutter project is opened, the analysis server re-analyses it from "
        + "source. Errors, completion and navigation are slower until that first pass finishes.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Dart analysis server, behind Dart and Flutter support in every editor",
        Publisher = "Google",
        Purpose = "The analysis server stores a summary of every file it has analysed, so that "
            + "reopening a package does not mean analysing it again from scratch. One store is "
            + "shared by every editor, and it keeps entries for every package you have ever opened.",
        Recommendation = "Nothing here originated with you: it is derived from Dart sources still "
            + "on your disk, by an analyser that is still installed, and the server rebuilds what "
            + "it needs without being asked. The cost is one slower analysis pass per project.",
    };

    /// <summary>
    /// §5.3. The analysis server runs as <c>dart</c>, started by whichever editor has a Dart or
    /// Flutter project open, and it holds this store open while it runs. An access-denied here is
    /// therefore an ordinary outcome rather than a failure.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["dart"];

    /// <summary>The <c>.dartServer</c> root. Exposed so tests can assert it is never targeted.</summary>
    public string RootPath => _root;

    /// <inheritdoc />
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        ToolRoot.Of(
            _root,
            "This is the Dart analysis server's own folder. Deguffer removes the caches inside it "
            + "and nothing else, because the server's own settings sit beside them.",
            DisposableChildren),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.DirectoryMayExist(_root));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (NothingToPlanFor(
                _root,
                "The Dart analysis server has no cache directory for this user.") is { } nothing)
        {
            return nothing;
        }

        // Moving the store onto another drive with a junction is how a developer keeps 3 GB off a
        // small system disk. The walk below would decline it too, but this says so about the whole
        // store in one sentence, and makes no claim about a .prompts that only resolves through the
        // link.
        if (LongPath.IsReparsePoint(_root))
        {
            return UnexaminedPlan(
                $"Leaving '{_root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var walk = CacheLevelWalk.Under(Levels, _root, ct);

        var spared = walk.Survivors(_declarations, ct);

        List<PlanNote> notes = [.. walk.Notes, .. spared.Select(CacheLevelWalk.SparedNote)];

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
            ProtectedPaths = BuildProtectedPaths(walk, spared),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = walk.Unreadable,
            WasNotExamined = walk.Targets.Count == 0 && walk.Declined.Count > 0,
        };
    }

    /// <summary>
    /// §5.6. The three unrecognised children are named rather than left to the Tier 4 default,
    /// because they are dot-named directories sitting directly beside the two that are removed —
    /// indistinguishable in shape from them, and so exactly what an over-broad rule takes along.
    /// Every other child the walk spared or declined is named for the same reason.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        LevelWalk walk,
        IReadOnlyList<(string Path, string Reason)> spared) => Protect(
        walk,
        spared,
        (_root, "The .dartServer root itself must survive — only its known-disposable children are removed."),
        (Path.Combine(_root, ".prompts"), "The user's answers to the analysis server's prompts — a preference, not a cache."),
        (Path.Combine(_root, ".plugin_manager"), "State for the analyzer plugins the server loads."),
        (Path.Combine(_root, ".instrumentation"), "The server's instrumentation log and the identifier it is keyed to."));
}
