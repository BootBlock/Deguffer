using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The caches Visual Studio keeps in a solution's <c>.vs</c> folder and rebuilds by itself, found
/// inside the source folders the user approved, and nothing else in that folder.
///
/// <para><b>Tier 1.</b> Every cache it offers is rebuilt the next time the solution opens, with no
/// command and no download: the design-time build and project evaluation caches, the Find in Files
/// index, and for C++ the browsing database and precompiled headers. What goes is a slower first
/// open. <see cref="VisualStudioCacheLayout"/> says what each is and why, and why the rest of the
/// folder stays.</para>
///
/// <para><b>Children, never the folder.</b> Every other source-folder row removes a whole directory its
/// project regenerates. A <c>.vs</c> folder is not one: about a quarter of it is Copilot conversations,
/// file snapshots, solution options and window layouts. So this is its own row rather than a
/// <see cref="BuildDirectoryProvider"/>, and the folder and everything it does not name stay.</para>
///
/// <para><b>In use is asked twice over.</b> Visual Studio works in the solution's folder, which the
/// process table sees, and <c>DevHub.exe</c> holds the Find in Files index open while working
/// somewhere else, which only Windows' answer about the index files sees. So every file in the caches
/// is a lock file (<see cref="VisualStudioCacheFiles"/>), and a folder either signal reports in use
/// is left whole.</para>
/// </summary>
public sealed class VisualStudioSolutionCacheProvider : CleanupProviderBase
{
    private static readonly string[] DirectoryNames = [VisualStudioCacheLayout.FolderName];

    private const string Subject = "a Visual Studio solution's .vs folder";

    private readonly SourceRootStore _roots;
    private readonly SourceDirectoryDiscovery _discovery;
    private readonly ILiveTreeInspector _liveTrees;
    private readonly TrackedFileCheck _tracked;

    private readonly PassMemo<IReadOnlyList<SourceRoot>> _approved = new();

    public VisualStudioSolutionCacheProvider(
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

    public override string Id => "visual-studio-solution-cache";

    public override string Name => "Visual Studio solution caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    /// <summary>One step per cache, each named with its solution, because the user knows a solution by name.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "The next time you open each solution, Visual Studio rebuilds what was removed: it evaluates the " +
        "projects and runs design-time builds again, indexes the files for Find in Files, and for C++ " +
        "rebuilds the browsing database and precompiled headers. That first open is slower, and Find in " +
        "Files and IntelliSense may be incomplete until it finishes. Your open documents, breakpoints, " +
        "window layout and Copilot conversations stay.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "Visual Studio",
        Publisher = "Microsoft",
        Purpose = "Visual Studio keeps a .vs folder beside each solution. Part of it is caches it "
            + "rebuilds by itself: the design-time build and project evaluation caches, the Find in "
            + "Files index, and for C++ the browsing database and precompiled headers.",
        Recommendation = "The rest of the folder is yours: Copilot conversations and file snapshots, "
            + "solution options and window layouts. Deguffer removes only the caches it recognises by "
            + "name and by the files inside them, only beside a solution, and only inside the folders "
            + "listed under Settings, Source folders.",
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
    /// Present once a folder has been approved. A solution whose Visual Studio has been uninstalled
    /// still has its <c>.vs</c> folder, so presence follows consent rather than the tool.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ApprovedRoots.Count > 0);

    /// <inheritdoc cref="BuildDirectoryProvider.IsAwaitingSourceFolders"/>
    public override bool IsAwaitingSourceFolders => ApprovedRoots.Count == 0;

    /// <summary>
    /// Every <c>.vs</c> folder the plan holds back because something is using it, or because whether
    /// anything is could not be asked, so that Explore refuses it and the solution folder holding it
    /// (§7.1). <see cref="InUseBuildDirectories"/> says how they are found.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        InUseBuildDirectories.DeclareAsync(
            _liveTrees,
            _discovery,
            ApprovedRoots,
            DirectoryNames,
            candidate => VisualStudioSolutionFolder.TryRecognise(candidate)?.Folder,
            Questions,
            ct);

    /// <summary>
    /// Every <c>.vs</c> folder below an approved source folder, which is the whole of where the search
    /// looks for what it removes. Named by name rather than by recognition, because a folder declined
    /// today is examined once a solution sits beside it.
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
                "Visual Studio solutions inside them, and nowhere else.");
        }

        var discovered = await _discovery.FindAsync(ApprovedRoots, ct).ConfigureAwait(false);

        var recognised = new List<VisualStudioSolutionFolder>();
        var declined = new List<string>();

        foreach (var candidate in discovered.Named(DirectoryNames))
        {
            ct.ThrowIfCancellationRequested();

            if (VisualStudioSolutionFolder.TryRecognise(candidate) is { } folder)
            {
                recognised.Add(folder);
            }
            else
            {
                declined.Add(candidate);
            }
        }

        // Counted before git's second opinion adds to the list, because each reason has its own
        // sentence, as DotNetObjProvider counts them.
        var unrecognised = declined.Count;

        // A .vs folder git tracks a file in is somebody's committed fixture, whatever is inside it.
        // Visual Studio's own templates ignore the folder, so its own output is never committed.
        var git = await _tracked.FindTrackedAsync([.. recognised.Select(folder => folder.Path)], ct).ConfigureAwait(false);

        foreach (var overruled in recognised.Where(folder => git.Tracked.Contains(folder.Path) || git.Unanswered.Contains(folder.Path)).ToList())
        {
            recognised.Remove(overruled);
            declined.Add(overruled.Path);
        }

        var live = LiveTreeVeto.Apply(
            _liveTrees,
            [.. recognised.Select(folder => new RecognisedBuildDirectory(folder.Path, folder.Folder))],
            Questions,
            ct);

        var cleared = live.Cleared.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        var examined = recognised.Where(folder => cleared.ContainsKey(folder.Path)).ToList();
        var contents = examined.ToDictionary(folder => folder.Path, folder => VisualStudioCacheExamination.Of(folder, ct), StringComparer.OrdinalIgnoreCase);

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. examined.SelectMany(folder => contents[folder.Path].Caches.Select(found => new DeletionTarget(
                    found.Path,
                    $"{found.Cache.Description} for {found.Solution}",
                    found.Cache.Kind == TargetKind.File ? FileAge(found.Path) : DirectoryAge.Of(found.Path, ct),
                    found.Cache.Kind,
                    Facets: [new ItemFacet("Solution", found.Solution)],
                    Group: ApprovedRootHeading.For(ApprovedRoots, found.Path),
                    UseCheck: cleared[folder.Path].StillUnused))),
            ],
            keep,
            ct).ConfigureAwait(false);

        var walked = examined.Select(folder => contents[folder.Path]).ToList();

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = BuildProtectedPaths(examined, walked, declined, live),
            Notes = SourceTreePlanNotes.For(
                discovered,
                $"'{VisualStudioCacheLayout.FolderName}'",
                Subject,
                unrecognised,
                live,
                measured.Note,
                [
                    .. TrackedFilePlanNotes.ForGit(git.Tracked.Count, git.Unanswered.Count, git.Unasked.Count),
                    .. walked.SelectMany(walk => walk.Notes),
                    .. steps.Count > 0 ? [KeptNote] : Array.Empty<PlanNote>(),
                ]),
            Fallback = measured.Fallback,
            HasUnreadableRoot =
                discovered.UnreadableDirectories.Count > 0
                || discovered.UnreachedRoots.Count > 0
                || walked.Any(walk => walk.Unreadable),

            // See BuildDirectoryProvider: a row whose only approved folder sits on a refused mount
            // must not render as "Already clear".
            WasNotExamined = steps.Count == 0 && discovered.RefusedRoots.Count > 0,
        };
    }

    /// <summary>
    /// Said once rather than per entry: forty solutions keep several hundred entries between them, and
    /// every one is asserted to survive whether or not it is named here.
    /// </summary>
    private static readonly PlanNote KeptNote = new(
        PlanNoteSeverity.Information,
        "Only the caches inside each .vs folder are removed. Copilot conversations and file snapshots, "
        + "solution options, window layouts and anything Deguffer does not recognise stay.");

    /// <summary>
    /// What the veto asks about a <c>.vs</c> folder in one reading of the machine: its solution
    /// folder, and every file in its caches, read from the disk as it is now. The same question at the
    /// preview, at the clean and for Explore.
    /// </summary>
    private static LiveTreeQuestion Questions(CancellationToken ct) =>
        LiveTreeQuestion.ForLockFiles(candidate => VisualStudioCacheFiles.Of(candidate.Path, ct));

    /// <summary>The file's last write, or null where it has gone, as <see cref="DirectoryAge"/> answers.</summary>
    private static DateTime? FileAge(string path)
    {
        var info = new FileInfo(LongPath.Extended(path));

        return info.Exists ? info.LastWriteTimeUtc : null;
    }

    /// <summary>
    /// §5.6. Around every <c>.vs</c> folder a cache was taken from: the solution folder and its
    /// solution files, which a rule reaching too far up would take, the <c>.vs</c> folder and the
    /// folders between it and each cache, which stay standing, and every entry the walk left alone,
    /// which are the caches' siblings. Every <c>.vs</c> folder declined or held back is
    /// protected whole, because it is separated from the ones examined only by evidence.
    /// </summary>
    private static IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        IReadOnlyList<VisualStudioSolutionFolder> examined,
        IReadOnlyList<VisualStudioCacheContents> walked,
        IReadOnlyList<string> declined,
        LiveTreeVetoResult live) => Protect(
    [
        .. examined.SelectMany(folder => new[]
        {
            (folder.Folder, $"The solution folder for {folder.Name}. Only caches inside its .vs folder are removed."),
        }.Concat(folder.SolutionFiles.Select(solution => (solution, "The solution file is yours, not a cache.")))),
        .. walked.SelectMany(walk => walk.Containers.Select(container =>
            (container, "Holds caches that are removed. The folder itself, and everything in it that is not a cache, stays."))),
        .. walked.SelectMany(walk => walk.Kept),
        .. declined.Select(path => (path, $"Not recognised as {Subject}, so it is left alone.")),
        .. live.Survivors,
    ]);
}
