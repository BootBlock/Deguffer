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
        IProcessInspector? inspector = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default)
    {
        _root = Path.Combine(Environment.UserProfile, ".gradle");
    }

    public override string Id => "gradle";

    public override string Name => "Gradle build cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next Gradle build re-downloads its dependencies and the wrapper distribution, then runs normally.";

    protected override IReadOnlyList<string> ConflictingProcessNames => ["java", "gradle", "studio64"];

    /// <summary>The <c>.gradle</c> root. Exposed so tests can assert it is never targeted.</summary>
    public string RootPath => _root;

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.DirectoryExists(_root));

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var steps = new List<CleanupStep>();
        var notes = new List<PlanNote>();

        if (!LongPath.DirectoryExists(_root))
        {
            return EmptyPlan("Gradle is not installed for this user — no .gradle directory.");
        }

        foreach (var child in EnumerateChildren())
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

            var bytes = await DirectorySizer.MeasureAsync(child.FullName, ct).ConfigureAwait(false);

            steps.Add(new DeleteDirectoryStep(child.FullName, classification.Reason)
            {
                EstimatedBytes = bytes,
            });
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
        };
    }

    /// <summary>
    /// §5.6. The root itself and the config beside it are the whole reason this provider is
    /// path-based rather than a recursive delete, so they are what the run has to prove.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths()
    {
        var candidates = new (string Path, string Reason)[]
        {
            (_root, "The .gradle root itself must survive — only its known-disposable children are removed."),
            (Path.Combine(_root, "gradle.properties"), "User configuration, which may hold signing keys and credentials."),
            (Path.Combine(_root, "init.d"), "User init scripts."),
            (Path.Combine(_root, "gradle.encrypted.properties"), "Encrypted user configuration."),
        };

        return
        [
            .. candidates.Select(c => new ProtectedPath(
                c.Path,
                c.Reason,
                LongPath.FileExists(c.Path) || LongPath.DirectoryExists(c.Path))),
        ];
    }

    private IEnumerable<DirectoryInfo> EnumerateChildren()
    {
        try
        {
            return new DirectoryInfo(LongPath.Extended(_root))
                .EnumerateDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }

    private CleanupPlan EmptyPlan(string why) => new()
    {
        ProviderId = Id,
        ProviderName = Name,
        Tier = Tier,
        WhatHappensOnNextUse = WhatHappensOnNextUse,
        Notes = [new PlanNote(PlanNoteSeverity.Information, why)],
    };
}
