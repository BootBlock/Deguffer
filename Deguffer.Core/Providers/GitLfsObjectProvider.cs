using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The old versions of large files Git LFS keeps in each repository it has downloaded them for, in
/// <c>.git\lfs\objects</c>, found in the source folders the user approved. Every fact about git-lfs
/// here is from its source, git-lfs at <c>debca01a</c>, and was observed against the 3.8.1 release.
///
/// <para><b>§5.1: Git LFS's own prune decides and removes.</b> It keeps the objects of every
/// worktree's checkout and index, of stashes, of every commit not yet pushed, and of refs and commits
/// inside its recent window (<c>lfs.fetchrecentrefsdays</c> and the rest, configured per
/// repository). Its dry run names each object it would remove, and Deguffer measures those files. The
/// clean is <c>git lfs prune --verify-remote</c>, which asks the remote for every object first and
/// removes nothing if one is missing there. Git LFS trusts a remote that is a folder without asking
/// it, so a store whose remote is not a server is not offered (<see cref="GitLfsRemote"/>).</para>
///
/// <para><b>§5.2: nothing in a git directory is a path target.</b> The source search seeks
/// <c>.git</c> and never enters it (<see cref="SourceTreeBoundary"/>). What is read inside one is
/// folder names, to find the stores (<see cref="GitLfsStores"/>), and git reads the rest. A folder
/// named <c>.git</c> with no <c>HEAD</c> is declined, and a store reached through a link, or moved by
/// <c>lfs.storage</c>, is not offered, because a store several repositories share must not be
/// pruned.</para>
///
/// <para><b>One item per git directory.</b> A repository and each submodule keep a store of their own,
/// and prune acts on one, so each is a separate choice under the source folder it was found in.</para>
/// </summary>
public sealed class GitLfsObjectProvider : CleanupProviderBase
{
    private const string GitDirectoryName = ".git";

    private static readonly IReadOnlyList<string> DirectoryNames = [GitDirectoryName];

    /// <summary>
    /// How many stores git is asked about at once (G4). Each store is a separate repository and no
    /// store's answer feeds another's question, so the row is as long as its slowest few stores rather
    /// than the sum of all of them. Each run is one short-lived git with its git-lfs child, and the
    /// slowest, the prune dry run, walks the repository's refs and recent history on disk, so more than a
    /// few at once would contend for that disk and for the processes the planner's other rows are
    /// starting beside this one (<see cref="CleanupPlanner.ProvidersAtOnce"/>).
    /// </summary>
    internal const int StoresAtOnce = 4;

    private readonly SourceRootStore _roots;
    private readonly SourceDirectoryDiscovery _discovery;
    private readonly ILiveTreeInspector _liveTrees;
    private readonly PassMemo<IReadOnlyList<SourceRoot>> _approved = new();

    public GitLfsObjectProvider(
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

        // As BuildDirectoryProvider does: the shared discovery ends up holding every row's names.
        _discovery = discovery ?? new SourceDirectoryDiscovery(Scanner, Volumes);
        _discovery.Include(DirectoryNames);
    }

    public override string Id => "git-lfs-objects";

    public override string Name => "Git LFS old file versions";

    /// <summary>Tier 2: a removed object is a download from the repository's server the next time a checkout needs it.</summary>
    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Items;

    /// <summary>
    /// Observed: the stores, the dry run's report, <c>git lfs env</c> and the prune itself were seen
    /// with git-lfs 3.8.1, on repositories and a submodule made for the purpose.
    /// </summary>
    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override string WhatHappensOnNextUse =>
        "Checking out an older commit downloads the large files it needs from the repository's server "
        + "again. The files your checkout uses, every file in a commit you have not pushed, and those of "
        + "recent commits and branches stay.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "Git LFS, the Git extension that keeps large files outside a repository's history",
        Publisher = "the Git LFS project, started by GitHub",
        Purpose = "Git LFS keeps a copy of every version of every large file it has downloaded for a "
            + "repository. In a game, art or machine-learning repository the old versions of its large "
            + "files can take more space than the files you have checked out.",
        Recommendation = "Deguffer runs Git LFS's own prune, which keeps what your checkout and your "
            + "unpushed and recent commits use, and removes an old version only after the repository's "
            + "server confirms it still has it. A repository whose remote is a folder rather than a server is "
            + "not offered, because Git LFS cannot check one.",
    };

    /// <summary>The roots the user approved. Empty means this provider does nothing.</summary>
    public IReadOnlyList<SourceRoot> ApprovedRoots => _approved.Get(() => _roots.Load());

    public override void InvalidateCaches()
    {
        base.InvalidateCaches();

        _liveTrees.Invalidate();
        _discovery.Invalidate();
        _approved.Forget();
    }

    /// <summary>
    /// Present once a folder has been approved and git is installed. Unlike a build directory, an old
    /// object is removed only by the tool, so without git there is nothing this row could do.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ApprovedRoots.Count > 0 && Environment.FindExecutable("git") is not null);

    public override bool IsAwaitingSourceFolders => ApprovedRoots.Count == 0;

    /// <summary>Every git directory below an approved folder, which holds every store prune can act on.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
            [.. ApprovedRoots.Select(root => CleanedPlace.FoldersNamed(root.Path, DirectoryNames))]);

    /// <summary>
    /// Every repository the plan holds back because something is working in it, or because whether
    /// anything is could not be asked, so that Explore refuses its git directory and its checkout
    /// (§7.1), as it refuses a build directory in use. A repository is a candidate where it keeps a
    /// store of its own, as the plan's are.
    ///
    /// <para>The repository's in-use question covers its submodules, whose checkouts are inside its
    /// own, and refusing its whole git directory covers their stores.</para>
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        InUseBuildDirectories.DeclareAsync(
            _liveTrees,
            _discovery,
            ApprovedRoots,
            DirectoryNames,
            Scanner.Tuning,
            candidate => GitLfsStores.In(candidate, ct) is { Recognised: true, Stores.Count: > 0 }
                ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(candidate))
                : null,
            InUse,
            ct);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (ApprovedRoots.Count == 0)
        {
            return EmptyPlan(
                "No source folders have been added yet. Add them in Settings and Deguffer will look for Git "
                + "repositories that use Git LFS inside them, and nowhere else.");
        }

        if (Environment.FindExecutable("git") is not { } git)
        {
            return EmptyPlan("Git is not installed on this machine.");
        }

        var discovered = await _discovery.FindAsync(ApprovedRoots, ct).ConfigureAwait(false);
        var found = new Findings();
        var declined = new List<string>();
        var stores = new List<GitLfsStore>();
        var unreachable = new List<string>();

        var candidates = discovered.Named(DirectoryNames);
        var surveys = await DiskBoundWork.EachAsync(candidates, Scanner.Tuning, candidate => GitLfsStores.In(candidate, ct), ct)
            .ConfigureAwait(false);

        for (var index = 0; index < candidates.Count; index++)
        {
            var survey = surveys[index];

            if (!survey.Recognised)
            {
                declined.Add(candidates[index]);
                continue;
            }

            stores.AddRange(survey.Stores);
            unreachable.AddRange(survey.Unreachable);

            foreach (var linked in survey.Linked)
            {
                found.Decline(linked, GitLfsPlanNotes.Linked(linked), GitLfsPlanNotes.SharedReason);
            }
        }

        found.Notes.AddRange(unreachable.Select(UnreadableRoot.UnreachedNote));

        var (checkouts, prunes) = stores.Count == 0 ? ([], []) : await ExamineAsync(git, stores, found, ct).ConfigureAwait(false);

        var live = LiveTreeVeto.Apply(
            _liveTrees,
            [.. prunes.Select(prune => new RecognisedBuildDirectory(prune.Checkout.Store.GitDirectory, prune.Checkout.WorkTree))],
            InUse,
            ct);

        var clear = live.Cleared.Select(cleared => cleared.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offered = await PreviewAsync(git, [.. prunes.Where(prune => clear.Contains(prune.Checkout.Store.GitDirectory))], found, ct)
            .ConfigureAwait(false);
        var (steps, measured) = await PlanStepsAsync(git, offered, ct).ConfigureAwait(false);

        if (stores.Count == 0 && found.Survivors.Count == 0 && unreachable.Count == 0)
        {
            found.Notes.Insert(0, GitLfsPlanNotes.NoStores);
        }

        if (steps.Count > 0)
        {
            found.Notes.Add(GitLfsPlanNotes.HowItRuns);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = await BuildProtectedPathsAsync(checkouts, declined, found.Survivors, live, ct).ConfigureAwait(false),
            Notes = SourceTreePlanNotes.For(
                discovered, $"'{GitDirectoryName}'", "a git directory", declined.Count, live, measured?.Note, found.Notes),
            Fallback = measured?.Fallback ?? FallbackReason.None,
            HasUnreadableRoot =
                discovered.UnreadableDirectories.Count > 0 || discovered.UnreachedRoots.Count > 0 || unreachable.Count > 0,

            // A store git or Git LFS could not answer for was not looked at, so a row holding only those
            // must not read "Already clear", as one whose folders are all on a refused mount must not.
            WasNotExamined = steps.Count == 0 && (discovered.RefusedRoots.Count > 0 || found.Unexamined > 0),
        };
    }

    /// <summary>What examining the stores found besides what it offers, gathered as it goes.</summary>
    private sealed class Findings
    {
        public List<PlanNote> Notes { get; } = [];

        public List<(string Path, string Reason)> Survivors { get; } = [];

        /// <summary>Stores git or Git LFS could not answer for, apart from those declined on what they said.</summary>
        public int Unexamined { get; private set; }

        public void Decline(GitLfsStore store, PlanNote? note, string reason)
        {
            if (note is not null)
            {
                Notes.Add(note);
            }

            Survivors.Add((store.Storage, reason));
        }

        public void Unanswered(GitLfsStore store, PlanNote? note)
        {
            Decline(store, note, GitLfsPlanNotes.NotPrunedReason);
            Unexamined++;
        }

        /// <summary>A store not offered: a warning where git could not answer, and information where it answered no.</summary>
        public void Refuse(GitLfsStore store, string why, bool unanswered)
        {
            if (unanswered)
            {
                Unanswered(store, new PlanNote(PlanNoteSeverity.Warning, why));
            }
            else
            {
                Decline(store, new PlanNote(PlanNoteSeverity.Information, why), GitLfsPlanNotes.NotPrunedReason);
            }
        }
    }

    /// <summary>A store whose dry run named at least one object to remove.</summary>
    private sealed record OfferedStore(GitLfsPrune Prune, IReadOnlyList<string> ObjectFiles);

    /// <summary>
    /// Where each store is checked out, and the prune of each one Git LFS can check against a server.
    /// Git LFS is asked once whether it is installed, and nothing is offered without it.
    /// </summary>
    /// <returns>Every store whose checkout git named, which the plan protects, and those of them it may prune.</returns>
    private async Task<(IReadOnlyList<GitLfsCheckout> Checkouts, IReadOnlyList<GitLfsPrune> Prunes)> ExamineAsync(
        string git,
        IReadOnlyList<GitLfsStore> stores,
        Findings found,
        CancellationToken ct)
    {
        var version = await Runner.RunAsync(git, "lfs version", GitRun.Query, ct).ConfigureAwait(false);

        if (!version.Succeeded)
        {
            found.Notes.Add(GitLfsPlanNotes.NotInstalled(stores.Count, version.Message));

            foreach (var store in stores)
            {
                found.Unanswered(store, note: null);
            }

            return ([], []);
        }

        var readings = await EachStoreAsync(stores, (store, token) => ReadStoreAsync(git, store, token), ct).ConfigureAwait(false);
        var checkouts = new List<GitLfsCheckout>();
        var prunes = new List<GitLfsPrune>();

        // In the stores' order, whatever order git answered in, so the plan reads the same on every pass.
        foreach (var (store, checkout, prune, refusal) in readings)
        {
            if (checkout is not null)
            {
                checkouts.Add(checkout);
            }

            if (prune is not null)
            {
                prunes.Add(prune);
            }
            else if (refusal is var (why, unanswered))
            {
                found.Refuse(store, why, unanswered);
            }
        }

        return (checkouts, prunes);
    }

    /// <summary>What git said of one store: where it is checked out, and its prune or why it has none.</summary>
    private sealed record StoreReading(
        GitLfsStore Store,
        GitLfsCheckout? Checkout,
        GitLfsPrune? Prune,
        (string WhyNot, bool Unanswered)? Refusal);

    /// <summary>One store's settings and then its remote, which needs the checkout the settings name.</summary>
    private async Task<StoreReading> ReadStoreAsync(string git, GitLfsStore store, CancellationToken ct)
    {
        var reading = await GitLfsSettings.ReadAsync(Runner, git, store, ct).ConfigureAwait(false);

        if (reading.Checkout is not { } checkout)
        {
            return new StoreReading(store, null, null, (reading.WhyNot, reading.Unanswered));
        }

        return await GitLfsRemote.ReadAsync(Runner, git, checkout, ct).ConfigureAwait(false) switch
        {
            { Prune: { } prune } => new StoreReading(store, checkout, prune, null),
            var refused => new StoreReading(store, checkout, null, (refused.WhyNot, refused.Unanswered)),
        };
    }

    /// <summary>Each store's dry run, and the object files it names.</summary>
    private async Task<IReadOnlyList<OfferedStore>> PreviewAsync(
        string git,
        IReadOnlyList<GitLfsPrune> prunes,
        Findings found,
        CancellationToken ct)
    {
        var outcomes = await EachStoreAsync(
            prunes,
            (prune, token) => Runner.RunAsync(git, prune.DryRunArguments, GitRun.Query, token),
            ct).ConfigureAwait(false);

        var offered = new List<OfferedStore>();

        for (var index = 0; index < prunes.Count; index++)
        {
            var (prune, outcome) = (prunes[index], outcomes[index]);
            var checkout = prune.Checkout;

            if (!outcome.Succeeded)
            {
                found.Unanswered(checkout.Store, GitLfsPlanNotes.DryRunFailed(checkout, outcome.Message));
            }
            else if (GitLfsPruneReport.TryRead(outcome.StandardOutput) is not { } preview)
            {
                found.Unanswered(checkout.Store, GitLfsPlanNotes.Unread(checkout));
            }
            else if (preview.Objects.Count > 0)
            {
                offered.Add(new OfferedStore(prune, [.. preview.Objects.Select(oid => ObjectFile(checkout.Store, oid))]));
            }
        }

        return offered;
    }

    /// <summary>
    /// <paramref name="ask"/> for every item, up to <see cref="StoresAtOnce"/> at a time, with the
    /// answers in the items' order rather than the order they arrived in.
    /// </summary>
    private static async Task<IReadOnlyList<TAnswer>> EachStoreAsync<TItem, TAnswer>(
        IReadOnlyList<TItem> items,
        Func<TItem, CancellationToken, Task<TAnswer>> ask,
        CancellationToken ct)
    {
        var answers = new TAnswer[items.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = StoresAtOnce, CancellationToken = ct };

        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), options, async (index, token) =>
            answers[index] = await ask(items[index], token).ConfigureAwait(false)).ConfigureAwait(false);

        return answers;
    }

    /// <summary>
    /// One prune per store, its figure the object files the dry run named, measured. The store is
    /// measured as well, as what the run compares against once prune has finished.
    /// </summary>
    private async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch? Measured)> PlanStepsAsync(
        string git,
        IReadOnlyList<OfferedStore> offered,
        CancellationToken ct)
    {
        if (offered.Count == 0)
        {
            return ([], null);
        }

        var measured = await MeasureAllAsync(
            [.. offered.SelectMany(store => store.ObjectFiles.Prepend(store.Prune.Checkout.Store.Objects))],
            ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>(offered.Count);
        var start = 0;

        foreach (var (prune, objectFiles) in offered)
        {
            var checkout = prune.Checkout;
            var repository = GitLfsPlanNotes.Describe(checkout);

            steps.Add(new RunCommandStep(git, prune.PruneArguments, $"Remove old Git LFS files from {repository} using Git LFS's own prune")
            {
                // Prune removes one file at a time and nothing else, so ended part-way it leaves fewer
                // old versions and no damaged store.
                WhenCancelled = CancelledCommand.Ended,
                Unset = GitRun.Unset,
                Output = GitRun.Output,
                Estimated = measured.TotalOf((start + 1)..(start + 1 + objectFiles.Count)),
                MeasuredPaths = [checkout.Store.Objects],
                MeasuredBefore = measured.Sizes[start],
                TargetCheck = new GitLfsPruneTarget(_liveTrees, checkout),
                Facets = [new ItemFacet("Repository", repository)],
                Group = ApprovedRootHeading.For(ApprovedRoots, checkout.WorkTree),
            });

            start += 1 + objectFiles.Count;
        }

        return (steps, measured);
    }

    /// <summary>
    /// §5.6. The work tree and the git directory, and in it what git needs to open the repository at
    /// all, must outlive a prune, which removes files from the object store and nothing else. So must
    /// the store's folder itself, and everything the plan declined or held back.
    /// </summary>
    private Task<IReadOnlyList<ProtectedPath>> BuildProtectedPathsAsync(
        IReadOnlyList<GitLfsCheckout> checkouts,
        IReadOnlyList<string> declined,
        IReadOnlyList<(string Path, string Reason)> survivors,
        LiveTreeVetoResult live,
        CancellationToken ct) => ProtectAsync(
    [
        .. checkouts.SelectMany(GitLfsPlanNotes.Survivors),
        .. declined.Select(path => (path, "Not recognised as a git directory, so it is left alone.")),
        .. survivors,
        .. live.Survivors,
    ],
    ct);

    /// <summary>Where Git LFS keeps the object <paramref name="oid"/>: <c>objects\aa\bb\&lt;oid&gt;</c> (<c>fs/fs.go</c>).</summary>
    private static string ObjectFile(GitLfsStore store, string oid) =>
        Path.Combine(store.Objects, oid[..2], oid[2..4], oid);

    /// <summary>
    /// What the in-use check asks about a store: whether a program is working in its work tree or its
    /// git directory. No lock file is named, because git holds none for longer than one command.
    /// <see cref="GitLfsPruneTarget"/> asks the same at the clean.
    /// </summary>
    private static LiveTreeQuestion InUse(CancellationToken ct) =>
        new(candidate => new LiveTreeQuery(candidate.Path, candidate.Project));
}
