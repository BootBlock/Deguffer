using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The task caches Nx keeps in the user's profile, one for each workspace, under
/// <c>%USERPROFILE%\.nx\&lt;id&gt;\cache</c>. Nx caps each at a tenth of the disk and trims it only
/// when that workspace runs, so a workspace that is gone keeps its cache for good. Researched from Nx's
/// source, because no Nx workspace was on the machine this was written against.
///
/// <para><b>Tier 1.</b> The cache holds the outputs and logs of tasks Nx ran, and Nx runs a task again
/// when its entry is missing. <see cref="NxCacheIndex"/> says why that holds only where the Nx reading
/// the index is 23.3 or later, and why a cache whose index an older Nx reads is left alone.</para>
///
/// <para><b>§5.1 was asked, and the answer is the path.</b> Nx's own eviction is
/// <c>nx reset --onlyCache</c>, which removes the same folder this does. It runs only inside a
/// workspace, with that workspace's Nx installed, and a folder here names no workspace to run it in
/// (<see cref="NxSharedCaches"/>). Where it could run, under Nx 23.2 or earlier it leaves the index
/// naming the entries it removed, which is the broken state this row refuses.</para>
///
/// <para><b>Children, never the root.</b> <c>~\.nx</c> and each workspace's folder stay. Only a folder
/// called <c>cache</c> that holds Nx's <c>terminalOutputs</c> goes, and its index, everything else in the
/// workspace's folder and every folder in <c>~\.nx</c> not named as Nx names one are asserted to survive
/// (§5.6).</para>
/// </summary>
public sealed class NxSharedCacheProvider : CleanupProviderBase
{
    private const string WorkspaceFacet = "Workspace ID";

    private const string RootReason =
        "This is Nx's folder in your profile. Deguffer removes only the task cache inside each workspace's "
        + "folder, never this folder or a workspace's index.";

    private const string WorkspaceReason =
        "Nx's folder for one workspace. Only the task cache inside it is removed.";

    private readonly ILiveTreeInspector _liveTrees;

    public NxSharedCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ILiveTreeInspector? liveTrees = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
        => _liveTrees = liveTrees ?? LiveTreeInspector.Default;

    public override string Id => "nx-shared-cache";

    public override string Name => "Nx shared task caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    /// <summary>One step per workspace, because a workspace's cache is the thing a user keeps or not.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "The next Nx run in each workspace runs its tasks again rather than restoring them from the cache, "
        + "so it takes longer once. Nx makes the cache again as it goes. Your workspaces, their task "
        + "history and Nx's settings are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "Nx, the JavaScript monorepo build system",
        Publisher = "Narwhal Technologies",
        Purpose = "From version 23.2, Nx keeps the outputs and logs of every task it runs in a folder in "
            + "your profile, one for each workspace, so that a task whose inputs have not changed is "
            + "restored rather than run. Each can grow to a tenth of the disk.",
        Recommendation = "Nx trims a workspace's cache only when that workspace runs, so a workspace you "
            + "have deleted keeps its cache for good. Deguffer removes a cache only where the version of "
            + "Nx that reads its index runs a task again when its entry is missing.",
    };

    /// <summary>Nx's per-user folder.</summary>
    public string Root => NxSharedCaches.Root(Environment);

    public override void InvalidateCaches()
    {
        _liveTrees.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>Present once Nx has made its folder in the profile, whether or not Nx is still installed.</summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.DirectoryMayExist(Root));

    /// <summary>Every <c>cache</c> folder below Nx's folder, which is the only name the clean removes.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([CleanedPlace.FoldersNamed(Root, [NxCacheFolder.Name])]);

    /// <summary>
    /// §5.2 as §7.1 reads it: <c>~\.nx</c> recognises each workspace folder holding a cache the plan
    /// would offer, and that folder recognises its <c>cache</c> and nothing else, so Explore cannot take
    /// an index or a whole workspace's folder. A cache the plan holds back is refused whole.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        // A refusal offers no roots, as an absence does: nothing behind it was classified.
        if (LongPath.ProbeDirectory(Root, out var rootIsLink) is not PathPresence.Present || rootIsLink is true)
        {
            return Task.FromResult<IReadOnlyList<ToolRoot>>([]);
        }

        var examination = NxSharedCaches.Examine(Root, ct);
        var live = Veto(examination, ct);

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. ToolRoot.WayDown(Root, live.Cleared.Select(cleared => cleared.Path), RootReason)
                .Select(level => level.Path.Equals(Root, StringComparison.OrdinalIgnoreCase) ? level : level with { Reason = WorkspaceReason }),
            .. live.Vetoed.Select(vetoed => new ToolRoot(vetoed.Directory, LiveTreeVeto.ProtectedReason, static _ => false)),
            .. live.Unchecked.Select(path => new ToolRoot(path, LiveTreeVeto.UncheckedReason, static _ => false)),
        ]);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var root = Root;

        if (NothingToPlanFor(root, $"Nx has not kept a cache in your profile: there is no {root} folder.", out var rootIsLink) is { } nothing)
        {
            return nothing;
        }

        // The root arrives by name, so nothing has classified it, and a cache behind a link would be
        // removed through it.
        if (rootIsLink)
        {
            return UnexaminedPlan(CacheLevelWalk.Note(root).Message);
        }

        var examination = NxSharedCaches.Examine(root, ct);
        var live = Veto(examination, ct);

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. live.Cleared.Select(cleared =>
                {
                    var workspace = NxSharedCaches.WorkspaceOf(cleared.Path);

                    return new DeletionTarget(
                        cleared.Path,
                        $"Nx task cache for workspace {workspace}",
                        DatedOneLevelDown: true,
                        Facets: [new ItemFacet(WorkspaceFacet, workspace)],
                        UseCheck: new NxIndexCheck(NxSharedCaches.IndexOf(cleared.Project), absentIsEmpty: true, cleared.StillUnused));
                }),
            ],
            keep,
            ct).ConfigureAwait(false);

        var notes = examination.Notes;

        if (LiveTreeVeto.NoteFor(live.Vetoed, vetoed => $"the Nx cache for workspace {NxSharedCaches.WorkspaceOf(vetoed.Directory)}") is { } held)
        {
            notes.Add(held);
        }

        if (LiveTreeVeto.UncheckedNote(live.Unchecked.Count) is { } incomplete)
        {
            notes.Add(incomplete);
        }

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (steps.Count > 0)
        {
            notes.Add(Unnamed);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = await ProtectAsync(
                [
                    (root, RootReason),
                    .. examination.Caches.Select(cache => (Path.GetDirectoryName(cache)!, WorkspaceReason)),
                    .. examination.Kept,
                    .. examination.Declined,
                    .. live.Survivors,
                ],
                ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = examination.Unreadable,
            WasNotExamined = steps.Count == 0 && examination.Declined.Count > 0,
        };
    }

    /// <summary>Why each cache is named by a number, said once rather than on every item.</summary>
    private static readonly PlanNote Unnamed = new(
        PlanNoteSeverity.Information,
        "Nx names each workspace's folder after a hash of the workspace's git remote or Nx Cloud id, which "
        + "cannot be traced back, so the caches are listed by that name. A workspace you still use makes "
        + "its cache again the next time it runs.");

    /// <summary>
    /// §5.3 for a cache: Nx holds its index open while it runs a task, and its daemon holds it for as
    /// long as it runs, wherever the workspace is. So the question is about those files, read from the
    /// disk again at the clean, because a database made after the preview is one Nx has open.
    /// </summary>
    private LiveTreeVetoResult Veto(NxSharedCacheExamination examination, CancellationToken ct) =>
        LiveTreeVeto.Apply(
            _liveTrees,
            [.. examination.Caches.Select(cache => new RecognisedBuildDirectory(cache, Path.GetDirectoryName(cache)!))],
            _ => LiveTreeQuestion.ForLockFiles(candidate => NxCacheIndex.Read(NxSharedCaches.IndexOf(candidate.Project)).Files),
            ct);
}
