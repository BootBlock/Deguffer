using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// .NET intermediate build output — the <c>obj</c> directory beside a project.
///
/// The first provider whose targets have no fixed location. Every other one knows where to look
/// because a toolchain owns a cache directory; this one looks only inside source folders the user
/// has explicitly approved, which is why <see cref="SourceRootStore"/> exists and why an empty
/// approval list means this provider finds nothing at all.
///
/// Tier 1, because regeneration needs no explicit command: a missing <c>obj</c> makes the next
/// build slower, where a missing Playwright browser makes the next run fail until someone reinstalls
/// it. Nothing inside a recognised one is unique — it is derived entirely from source and the
/// package cache, both of which are still present.
///
/// Recognition is <see cref="DotNetIntermediateSignature"/>'s job and lives there; discovery is
/// <see cref="SourceDirectoryDiscovery"/>'s. This holds the rules about what to do with the answers.
/// </summary>
public sealed class DotNetObjProvider : CleanupProviderBase
{
    private const string DirectoryName = "obj";

    private static readonly string[] DirectoryNames = [DirectoryName];

    private readonly SourceRootStore _roots;
    private readonly SourceDirectoryDiscovery _discovery;
    private readonly ILiveTreeInspector _liveTrees;
    private readonly TrackedFileCheck _tracked;

    private IReadOnlyList<string>? _approved;

    public DotNetObjProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
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
        ArgumentNullException.ThrowIfNull(roots);

        _roots = roots;
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
        _discovery = discovery ?? new SourceDirectoryDiscovery(Scanner);
        _discovery.Include(DirectoryNames);
        _tracked = new TrackedFileCheck(Environment, Runner);
    }

    public override string Id => "dotnet-obj";

    public override string Name => ".NET intermediate build output";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    /// <summary>
    /// §7. The caveat is stated plainly rather than promising purely local regeneration: restore
    /// normally resolves from the NuGet global packages cache and is fully offline, but cleaning
    /// that cache in the same run leaves nothing local to resolve from.
    /// </summary>
    public override string WhatHappensOnNextUse =>
        "The next build of each project regenerates its intermediate output, so that build is slower. " +
        "Nothing here is unique — it is derived from your source and the NuGet cache. " +
        "If you also clear the NuGet cache in this run, the next restore needs the network, and a " +
        "project whose feed is unreachable will not rebuild until that is resolved.";

    /// <summary>The roots the user approved. Empty means this provider does nothing.</summary>
    public IReadOnlyList<string> ApprovedRoots => _approved ??= _roots.Load();

    public override void InvalidateCaches()
    {
        base.InvalidateCaches();

        _liveTrees.Invalidate();
        _discovery.Invalidate();

        // Re-read on the next pass, so a root added in Settings is picked up without a restart.
        _approved = null;
    }

    /// <summary>
    /// Present if the SDK is installed, or if folders have been approved regardless.
    ///
    /// Every other provider answers this with "is the toolchain here", and so does this one — but
    /// it matters more, because an unconfigured provider reported absent is invisible, and a user
    /// would have no way to discover that approving a folder is what makes it work. Keying on the
    /// SDK shows the guidance to the developers it applies to without putting a permanently empty
    /// row in front of anyone who has never built .NET.
    ///
    /// Approved roots alone are also enough: the user has said this matters to them, and an SDK
    /// uninstalled after the fact should not silently orphan the folders they chose.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ApprovedRoots.Count > 0 || Environment.FindExecutable("dotnet") is not null);

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        if (ApprovedRoots.Count == 0)
        {
            return EmptyPlan(
                "No source folders have been added yet. Add them in Settings and Deguffer will look " +
                "for build output inside them, and nowhere else.");
        }

        var discovered = await _discovery.FindAsync(ApprovedRoots, ct).ConfigureAwait(false);

        // One list of pairs rather than two lists held in step. The directory and the project it
        // belongs to are used together everywhere below, and keeping them aligned by index would
        // make a mismatched pair — a step deleting one path while protecting another project's
        // files — a plausible result of an ordinary edit.
        var targets = new List<RecognisedObj>();
        var declined = new List<string>();

        foreach (var candidate in discovered.Named(DirectoryNames))
        {
            ct.ThrowIfCancellationRequested();

            if (DotNetIntermediateSignature.TryRecognise(candidate, ct) is { } project)
            {
                targets.Add(new RecognisedObj(candidate, project));
            }
            else
            {
                declined.Add(candidate);
            }
        }

        // §7's second opinion, applied after recognition so it runs over a handful of directories
        // rather than every candidate.
        var git = await _tracked.FindTrackedAsync([.. targets.Select(t => t.Path)], ct).ConfigureAwait(false);

        // Counted before the cross-check adds to the list, because each reason gets its own sentence
        // in the plan. A directory recognition confirmed and git then overruled must not also be
        // reported as one recognition could not confirm — that states one directory as two, under a
        // reason that is untrue of it.
        var unrecognised = declined.Count;

        // A directory the check could not cover is declined alongside one it ruled out. Both are
        // "the second opinion did not clear this", and only the reason the user is told differs.
        var cleared = new List<RecognisedObj>(targets.Count);

        foreach (var target in targets)
        {
            if (git.Tracked.Contains(target.Path) || git.Unanswered.Contains(target.Path))
            {
                declined.Add(target.Path);
            }
            else
            {
                cleared.Add(target);
            }
        }

        targets = cleared;

        // §5.3, and it carries more force here than for a cache: removing intermediate output under
        // a live build breaks that build in flight rather than leaving stale state behind. This
        // replaces the list of process names this provider used to carry, which asked a question
        // that was wrong in both directions — one MSBuild anywhere on the machine warned about every
        // project on the disk, while the compiler server actually holding a directory open need not
        // be called any of them.
        var live = LiveTreeVeto.Apply(
            _liveTrees,
            [.. targets.Select(t => new RecognisedBuildDirectory(
                t.Path,
                Path.GetDirectoryName(t.Project.ProjectFilePath)!,
                "Intermediate build output"))],
            [],
            ct);

        var vetoed = live.Vetoed.Select(v => v.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        targets = [.. targets.Where(t => !vetoed.Contains(t.Path))];

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. targets.Select(t => new DeletionTarget(
                    t.Path,
                    $"Intermediate build output for {t.Project.ProjectName}",
                    BuildDirectoryAge.Of(t.Path, ct))),
            ],
            ct).ConfigureAwait(false);

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = BuildProtectedPaths(targets, declined, live),
            Notes = SourceTreePlanNotes.For(
                discovered,
                $"'{DirectoryName}'",
                ".NET intermediate build output",
                unrecognised,
                live,
                measured.Note,
                BuildRunningProcessNote(),
                ObjPlanNotes.ForGit(git.Tracked.Count, git.Unanswered.Count)),
            Fallback = measured.Fallback,
        };
    }

    /// <summary>One directory that proved its identity, and the project that proved it.</summary>
    private readonly record struct RecognisedObj(string Path, RecognisedProject Project);

    /// <summary>
    /// §5.6. Three things around every directory removed have to survive, and each is a different
    /// way an over-broad rule could reach too far: the project directory (deleting the parent
    /// instead of the child), the project file itself (the source the output was derived from), and
    /// <c>bin</c> (the sibling that looks equivalent and is not — it can hold hand-placed native
    /// dependencies and copied assets that no build reproduces, which is why it is out of scope).
    ///
    /// Every declined candidate is protected by name as well, and so is every directory held back
    /// because something is using it. They are directories of the same name, often in the same tree,
    /// separated from the targets only by evidence — which is exactly the situation where an
    /// over-broad rule takes one with the other.
    /// </summary>
    private static IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        IReadOnlyList<RecognisedObj> targets,
        IReadOnlyList<string> declined,
        LiveTreeVetoResult live) => Protect(
    [
        .. targets.Select(t => t.Project).SelectMany(project =>
        {
            var directory = Path.GetDirectoryName(project.ProjectFilePath)!;

            return new (string, string)[]
            {
                (directory, $"The project directory for {project.ProjectName} — only its build output is removed."),
                (project.ProjectFilePath, "The project file itself is source, not build output."),
                (Path.Combine(directory, "bin"), "Output directories are out of scope: they can hold files no build reproduces."),
            };
        }),
        .. declined.Select(path => (path, "Not recognised as .NET intermediate build output, so it is left alone.")),
        .. live.Vetoed.Select(v => (v.Directory, LiveTreeVeto.ProtectedReason)),
    ]);
}
