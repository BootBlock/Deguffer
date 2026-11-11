using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The task cache Nx keeps inside a workspace, <c>.nx\cache</c>, found inside the source folders the
/// user approved. Nx 17 to 23.1 keep every workspace's cache there. From 23.2 Nx keeps it in the
/// profile instead (<see cref="NxSharedCacheProvider"/>), and a workspace still uses its own when it has
/// no git remote or Nx Cloud id, or sets where its cache goes. A workspace that has moved to the
/// profile also keeps the one it had, which nothing reads unless the profile becomes unusable.
///
/// <para><b>Tier 1, under the same rule as the shared cache.</b> The index is in
/// <c>.nx\workspace-data</c> unless a variable moves it (<see cref="NxWorkspaceFolder.IndexIn"/>), and
/// <see cref="NxCacheIndex"/> says why a cache an older Nx indexes is left alone: Nx 19 to 23.2 would
/// report its tasks as restored and restore nothing. So is a cache whose index is not where it should
/// be, because a variable set only in a shell can have moved it.</para>
///
/// <para><b>The cache, never the folder.</b> <c>.nx</c> also holds the index and the daemon's logs, and
/// the Nx wrapper's files, which a workspace commits (<see cref="NxWorkspaceFolder"/>). So this is its
/// own row rather than a <see cref="BuildDirectoryProvider"/>, which removes the folder it finds, and
/// everything in <c>.nx</c> but <c>cache</c> is asserted to survive.</para>
///
/// <para><b>In use is asked twice over.</b> A run and the daemon work in the workspace, which the
/// process table sees, and hold the index open, which Windows' answer about the databases sees.</para>
/// </summary>
public sealed class NxWorkspaceCacheProvider : CleanupProviderBase
{
    private static readonly string[] DirectoryNames = [NxWorkspaceFolder.FolderName];

    private const string Subject = "an Nx workspace's .nx folder";

    private const string WorkspaceFacet = "Workspace";

    private readonly SourceRootStore _roots;
    private readonly SourceDirectoryDiscovery _discovery;
    private readonly ILiveTreeInspector _liveTrees;
    private readonly TrackedFileCheck _tracked;

    private readonly PassMemo<IReadOnlyList<SourceRoot>> _approved = new();

    public NxWorkspaceCacheProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        ArgumentNullException.ThrowIfNull(roots);

        _roots = roots;
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
        _discovery = discovery ?? new SourceDirectoryDiscovery(Scanner, Volumes);
        _discovery.Include(DirectoryNames);
        _tracked = new TrackedFileCheck(Environment, Runner);
    }

    public override string Id => "nx-workspace-cache";

    public override string Name => "Nx workspace task caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    /// <summary>One step per workspace, named with it, because the user knows a workspace by name.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "The next Nx run in each workspace runs its tasks again rather than restoring them from its cache, "
        + "so it takes longer once. Nx makes the cache again as it goes. The workspace, its index and "
        + "the rest of its .nx folder are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "Nx, the JavaScript monorepo build system",
        Publisher = "Narwhal Technologies",
        Purpose = "Nx keeps the outputs and logs of every task it runs in a .nx\\cache folder in the "
            + "workspace, so that a task whose inputs have not changed is restored rather than run. "
            + "Versions before 23.2 always keep it there.",
        Recommendation = "Deguffer offers one only beside a workspace's nx.json, only where Nx made its "
            + "terminalOutputs folder inside it and the version of Nx that reads its index runs a task "
            + "again when its entry is missing, and only inside the folders listed under Settings, "
            + "Source folders.",
    };

    /// <summary>The roots the user approved. Empty means this provider does nothing.</summary>
    public IReadOnlyList<SourceRoot> ApprovedRoots => _approved.Get(() => _roots.Load());

    public override void InvalidateCaches()
    {
        base.InvalidateCaches();

        _liveTrees.Invalidate();
        _discovery.Invalidate();

        // Re-read on the next pass, so a root added in Settings is picked up without a restart.
        _approved.Forget();
    }

    /// <summary>
    /// Present once a folder has been approved. A workspace whose Nx has been removed still has its
    /// cache, so presence follows consent rather than the tool.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ApprovedRoots.Count > 0);

    /// <inheritdoc cref="BuildDirectoryProvider.IsAwaitingSourceFolders"/>
    public override bool IsAwaitingSourceFolders => ApprovedRoots.Count == 0;

    /// <summary>
    /// Every <c>.nx</c> folder the plan holds back because something is using it, or because whether
    /// anything is could not be asked, so that Explore refuses it and the workspace holding it (§7.1).
    /// <see cref="InUseBuildDirectories"/> says how they are found.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        InUseBuildDirectories.DeclareAsync(
            _liveTrees,
            _discovery,
            ApprovedRoots,
            DirectoryNames,
            Scanner.Tuning,
            candidate => NxWorkspaceFolder.TryRecognise(candidate)?.Workspace,
            Questions,
            ct);

    /// <summary>
    /// Every <c>.nx</c> folder below an approved source folder, which is the whole of where the search
    /// looks for what it removes. Named by name rather than by recognition, because a folder declined
    /// today is examined once its workspace gains what it lacks.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
            [.. ApprovedRoots.Select(root => CleanedPlace.FoldersNamed(root.Path, DirectoryNames))]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (ApprovedRoots.Count == 0)
        {
            return EmptyPlan(
                "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
                "Nx workspaces inside them, and nowhere else.");
        }

        var discovered = await _discovery.FindAsync(ApprovedRoots, ct).ConfigureAwait(false);

        var recognised = new List<NxWorkspaceFolder>();
        var declined = new List<string>();

        var candidates = discovered.Named(DirectoryNames);
        var folders = await DiskBoundWork.EachAsync(candidates, Scanner.Tuning, NxWorkspaceFolder.TryRecognise, ct).ConfigureAwait(false);

        for (var index = 0; index < candidates.Count; index++)
        {
            if (folders[index] is { } folder)
            {
                recognised.Add(folder);
            }
            else
            {
                declined.Add(candidates[index]);
            }
        }

        // Counted before the cache's own evidence and git's opinion add to what is left alone, because
        // each reason has its own sentence, as DotNetObjProvider counts them.
        var unrecognised = declined.Count;

        var cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refused = new List<(string Path, string Reason)>();

        foreach (var folder in recognised)
        {
            // A missing index is not read as no index here: see NxCacheIndex.AllowsRemoval.
            var verdict = NxCacheFolder.Examine(folder.Cache, folder.IndexIn(Environment), absentIsEmpty: false);

            if (verdict.Declined is { } reason)
            {
                refused.Add(reason);
            }

            if (verdict.Cache is not null)
            {
                cached.Add(folder.Path);
            }
        }

        recognised.RemoveAll(folder => !cached.Contains(folder.Path));

        // Nx ignores its cache in every workspace it creates, so a cache git tracks a file in is
        // somebody's fixture. Asked of the cache, never the folder: the Nx wrapper's files beside it are
        // committed on purpose.
        var git = await _tracked.FindTrackedAsync([.. recognised.Select(folder => folder.Cache)], ct).ConfigureAwait(false);

        foreach (var overruled in recognised.Where(folder => git.Tracked.Contains(folder.Cache) || git.Unanswered.Contains(folder.Cache)).ToList())
        {
            recognised.Remove(overruled);
            declined.Add(overruled.Cache);
        }

        var live = LiveTreeVeto.Apply(
            _liveTrees,
            [.. recognised.Select(folder => new RecognisedBuildDirectory(folder.Path, folder.Workspace))],
            Questions,
            ct);

        var cleared = live.Cleared.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        var examined = recognised.Where(folder => cleared.ContainsKey(folder.Path)).ToList();

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. examined.Select(folder => new DeletionTarget(
                    folder.Cache,
                    $"Nx task cache for {folder.Name}",
                    DatedOneLevelDown: true,
                    Facets: [new ItemFacet(WorkspaceFacet, folder.Name)],
                    Group: ApprovedRootHeading.For(ApprovedRoots, folder.Cache),
                    UseCheck: new NxIndexCheck(folder.IndexIn(Environment), absentIsEmpty: false, cleared[folder.Path].StillUnused))),
            ],
            keep,
            ct).ConfigureAwait(false);

        var siblings = examined.Select(Siblings).ToList();

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = await ProtectAsync(
                [
                    .. examined.SelectMany(folder => new[]
                    {
                        (folder.Workspace, $"The Nx workspace {folder.Name}. Only the task cache in its .nx folder is removed."),
                        (Path.Combine(folder.Workspace, NxWorkspaceFolder.Manifest), "The workspace's Nx configuration, not a cache."),
                        (folder.Path, "Holds the task cache that is removed. The folder itself, and everything in it that is not the cache, stays."),
                        (folder.IndexIn(Environment), "The workspace's Nx index, which holds its task history as well. The Nx that reads it treats a removed entry as a miss."),
                    }),
                    .. siblings.SelectMany(sibling => sibling.Kept),
                    .. refused,
                    .. declined.Select(path => (path, $"Not recognised as {Subject} with a cache Deguffer can remove, so it is left alone.")),
                    .. live.Survivors,
                ],
                ct).ConfigureAwait(false),
            Notes = SourceTreePlanNotes.For(
                discovered,
                $"'{NxWorkspaceFolder.FolderName}'",
                Subject,
                unrecognised,
                live,
                measured.Note,
                [
                    .. TrackedFilePlanNotes.ForGit(git.Tracked.Count, git.Unanswered.Count, git.Unasked.Count),
                    .. refused.Select(r => new PlanNote(PlanNoteSeverity.Information, $"Leaving '{r.Path}' alone: {r.Reason}")),
                    .. siblings.Where(sibling => sibling.Unreadable).Select(sibling => UnreadableRoot.Note(sibling.Folder)),
                ]),
            Fallback = measured.Fallback,
            HasUnreadableRoot =
                discovered.UnreadableDirectories.Count > 0
                || discovered.UnreachedRoots.Count > 0
                || siblings.Any(sibling => sibling.Unreadable),

            // See BuildDirectoryProvider: a row whose only approved folder sits on a refused mount
            // must not render as "Already clear".
            WasNotExamined = steps.Count == 0 && (discovered.RefusedRoots.Count > 0 || refused.Count > 0),
        };
    }

    /// <summary>
    /// What the veto asks about a <c>.nx</c> folder in one reading of the machine: its workspace, and
    /// the databases of its index, read from the disk as they are now.
    /// </summary>
    private LiveTreeQuestion Questions(CancellationToken ct) =>
        LiveTreeQuestion.ForLockFiles(candidate =>
            NxCacheIndex.Read(new NxWorkspaceFolder(candidate.Path, candidate.Project).IndexIn(Environment)).Files);

    /// <summary>
    /// Everything in the <c>.nx</c> folder beside the cache, which §5.6 asserts survived: the index, the
    /// daemon's logs and the wrapper's files are the siblings a rule reaching one folder too far takes.
    /// </summary>
    private static NxWorkspaceSiblings Siblings(NxWorkspaceFolder folder) =>
        FolderEntries.Of(folder.Path) is { } entries
            ? new NxWorkspaceSiblings(
                folder.Path,
                [
                    .. entries
                        .Where(entry => !entry.Name.Equals(NxCacheFolder.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(entry => (Path.Combine(folder.Path, entry.Name), "Part of the workspace's Nx folder, not its task cache.")),
                ],
                Unreadable: false)
            : new NxWorkspaceSiblings(folder.Path, [], Unreadable: true);

    private sealed record NxWorkspaceSiblings(string Folder, IReadOnlyList<(string Path, string Reason)> Kept, bool Unreadable);
}
