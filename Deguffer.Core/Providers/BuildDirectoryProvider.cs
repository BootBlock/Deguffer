using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A whole build directory that a toolchain regenerates, found inside the source folders the user
/// approved — Unity's <c>Library</c>, Cargo's <c>target</c>, a <c>node_modules</c>, a <c>.venv</c>.
///
/// <para>Four things are the same for every one of them and are done here: search the approved roots
/// and nowhere else, prove the directory's identity from the project around it, refuse anything
/// something is using, and turn what is left into steps that promise the source survives. What
/// differs is a <see cref="BuildDirectoryKind"/>, a tier, and the sentence saying what the next use
/// costs — so a subclass is a declaration rather than an algorithm.</para>
///
/// <para>Deliberately not shared with <see cref="DotNetObjProvider"/>, whose identity check is a
/// different thing entirely: three files inside the directory that must agree on a project name,
/// then git asked for a second opinion. The parts that carry safety — the live-tree veto, the
/// wording of the notes, how an age is read — are shared with it as separate seams instead, so that
/// what is common is common because it is one rule and not because two classes happen to look
/// alike.</para>
/// </summary>
public abstract class BuildDirectoryProvider : CleanupProviderBase
{
    private readonly SourceRootStore _roots;
    private readonly SourceDirectoryDiscovery _discovery;

    private IReadOnlyList<string>? _approved;

    /// <param name="kind">
    /// Supplied rather than read from an overridable member, because the discovery this constructor
    /// builds needs it, and a subclass's own property is not answerable until its constructor runs.
    /// </param>
    protected BuildDirectoryProvider(
        BuildDirectoryKind kind,
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
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(roots);

        Kind = kind;
        _roots = roots;
        LiveTrees = liveTrees ?? LiveTreeInspector.Default;

        // An unshared discovery is correct but pays for its own pass, which is what a test wants
        // and what production must not have. Either way this provider's own names go in, so the
        // shared one ends up holding the union of what every provider looks for.
        _discovery = discovery ?? new SourceDirectoryDiscovery(Scanner);
        _discovery.Include(Kind.DirectoryNames);
    }

    /// <summary>What proves a candidate directory is this toolchain's (§5.2).</summary>
    protected BuildDirectoryKind Kind { get; }

    /// <summary>
    /// What a recognised one is, completing "could not be confirmed as …" and describing each step.
    /// Written for the user, so it reads as a noun phrase rather than as an identifier.
    /// </summary>
    protected abstract string Subject { get; }

    /// <summary>
    /// The guidance shown when no source folder has been approved. A provider whose subject the user
    /// has never heard of should say what it would look for, not merely that it found nothing.
    /// </summary>
    protected abstract string NothingApprovedGuidance { get; }

    private ILiveTreeInspector LiveTrees { get; }

    /// <summary>The roots the user approved. Empty means this provider does nothing.</summary>
    public IReadOnlyList<string> ApprovedRoots => _approved ??= _roots.Load();

    public override void InvalidateCaches()
    {
        base.InvalidateCaches();

        LiveTrees.Invalidate();
        _discovery.Invalidate();

        // Re-read on the next pass, so a root added in Settings is picked up without a restart.
        _approved = null;
    }

    /// <summary>
    /// Present once a folder has been approved.
    ///
    /// Unlike a cache provider, this cannot key on the toolchain being installed. A developer who
    /// has uninstalled Unity still has the <c>Library</c> directories it left behind, and those are
    /// exactly the ones worth reclaiming — so presence follows consent rather than the tool.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ApprovedRoots.Count > 0);

    /// <summary>
    /// Absence here means Deguffer has not been told where to look, never that the tool is gone —
    /// <see cref="IsPresentAsync"/> above does not consult the tool at all. A shell that treated
    /// the two alike would drop the largest reclaimable thing on the machine from the list, and
    /// call it "not installed" on the way out.
    /// </summary>
    public override bool IsAwaitingSourceFolders => ApprovedRoots.Count == 0;

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (ApprovedRoots.Count == 0)
        {
            return EmptyPlan(NothingApprovedGuidance);
        }

        var discovered = await _discovery.FindAsync(ApprovedRoots, ct).ConfigureAwait(false);

        var recognised = new List<RecognisedBuildDirectory>();
        var declined = new List<string>();

        foreach (var candidate in discovered.Named(Kind.DirectoryNames))
        {
            ct.ThrowIfCancellationRequested();

            if (BuildDirectorySignature.TryRecognise(Kind, candidate, ct) is { } project)
            {
                recognised.Add(new RecognisedBuildDirectory(candidate, project));
            }
            else
            {
                declined.Add(candidate);
            }
        }

        var live = LiveTreeVeto.Apply(LiveTrees, recognised, Kind.LockFiles, ct);

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. live.Cleared.Select(target => new DeletionTarget(
                    target.Path,
                    $"{Subject} for {Path.GetFileName(target.Project)}",
                    DirectoryAge.Of(target.Path, ct))),
            ],
            keep,
            ct).ConfigureAwait(false);

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = BuildProtectedPaths(live, declined),
            Notes = SourceTreePlanNotes.For(
                discovered, Kind.DisplayNames, Subject, declined.Count, live, measured.Note),
            Fallback = measured.Fallback,
            HasUnreadableRoot = discovered.UnreadableDirectories.Count > 0,
        };
    }

    /// <summary>
    /// §5.6, and here the protected path is the user's own source rather than a config file.
    ///
    /// Three groups, each a different way an over-broad rule could reach too far. The project folder
    /// is the parent whose name proved the child's identity, so a rule that deleted it instead would
    /// take the whole project. Every marker the recognition relied on is named individually, because
    /// those are the files a build reads and the ones whose loss would make the directory
    /// unregenerable — the very thing the tier claims it is not. And every directory that was
    /// declined or held back is protected by name: they are directories of the same name, often in
    /// the same tree, separated from the targets only by evidence, which is exactly the situation
    /// where an over-broad rule takes one with the other.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        LiveTreeVetoResult live,
        IReadOnlyList<string> declined) => Protect(
    [
        .. live.Cleared.SelectMany(target => new[]
        {
            (target.Project, $"The project folder for {Path.GetFileName(target.Project)} — only its build output is removed."),
        }.Concat(Markers.Select(marker =>
            (Path.Combine(target.Project, marker),
             $"{marker} is source, and is what the build output is regenerated from.")))),
        .. declined.Select(path => (path, $"Not recognised as {Subject}, so it is left alone.")),
        .. live.Vetoed.Select(vetoed => (vetoed.Directory, LiveTreeVeto.ProtectedReason)),
    ]);

    /// <summary>
    /// Everything beside the directory that has to survive it: every sibling the recognition looked
    /// at, and every one the kind names as a survivor without recognising by. The alternatives are
    /// included because §5.6 asserts survival of what exists, and only one of them will.
    /// </summary>
    private IReadOnlyList<string> Markers =>
        [.. Kind.RequiredSiblings, .. Kind.AnyOfSiblings, .. Kind.ProtectedSiblings];
}
