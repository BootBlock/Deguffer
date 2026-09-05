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

    private readonly string _root;

    public DartAnalysisServerProvider(
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
        _root = Path.Combine(Environment.LocalAppData, ".dartServer");
    }

    public override string Id => "dart-analysis-server";

    public override string Name => "Dart analysis server cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

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
        Task.FromResult(LongPath.DirectoryExists(_root));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (!LongPath.DirectoryExists(_root))
        {
            return EmptyPlan("The Dart analysis server has no cache directory for this user.");
        }

        // Moving the store onto another drive with a junction is how a developer keeps 3 GB off a
        // small system disk, and the enumeration below never classifies the directory it is handed:
        // it would return the far side's ordinary children, target the recognised ones, and pass
        // every §5.6 assertion, because each survivor named here resolves through the same link.
        if (LongPath.IsReparsePoint(_root))
        {
            return UnexaminedPlan(
                $"Leaving '{_root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();

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

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

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
            WasNotExamined = targets.Count == 0 && scan.Links.Count > 0,
        };
    }

    /// <summary>
    /// §5.6. The three unrecognised children are named rather than left to the Tier 4 default,
    /// because they are dot-named directories sitting directly beside the two that are removed —
    /// indistinguishable in shape from them, and so exactly what an over-broad rule takes along.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths() => Protect(
        (_root, "The .dartServer root itself must survive — only its known-disposable children are removed."),
        (Path.Combine(_root, ".prompts"), "The user's answers to the analysis server's prompts — a preference, not a cache."),
        (Path.Combine(_root, ".plugin_manager"), "State for the analyzer plugins the server loads."),
        (Path.Combine(_root, ".instrumentation"), "The server's instrumentation log and the identifier it is keyed to."));
}
