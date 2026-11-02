using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// pre-commit's store: one clone of every hook repository a <c>.pre-commit-config.yaml</c> named, each
/// with the environment pre-commit installed for it. Researched from pre-commit's source
/// (<c>store.py</c>, <c>commands/gc.py</c>, <c>commands/clean.py</c>, <c>staged_files_only.py</c>,
/// November 2026) rather than measured, because no pre-commit was installed on the machine this was
/// written against.
///
/// <para><b>Where it is.</b> <c>_get_default_directory</c> takes <c>PRE_COMMIT_HOME</c>, then
/// <c>XDG_CACHE_HOME\pre-commit</c>, then <c>~\.cache\pre-commit</c>, and an empty variable counts as
/// unset. Both variables are read as the tool Deguffer starts sees them, because the store examined
/// has to be the one <c>gc</c> acts on.</para>
///
/// <para><b>§5.1, through <c>pre-commit gc</c> only.</b> <c>gc</c> keeps every repository a configuration
/// recorded in the store's <c>db.db</c> still names, forgets each configuration that no longer loads,
/// and removes the rest, under the store's own <c>.lock</c>. Removing a repository folder by path
/// instead leaves its row in <c>db.db</c>, and every project naming it then fails until the store is
/// cleaned, so nothing here is removed by path and Explore refuses all of the store.</para>
///
/// <para><b>Never <c>pre-commit clean</c>.</b> It deletes the whole store, and the store also holds
/// <c>patch&lt;epoch&gt;-&lt;pid&gt;</c> files: unstaged changes pre-commit stashed during a commit and
/// never removes, which can be the only copy of that work.</para>
///
/// <para><b>The figure is a ceiling.</b> <c>gc</c> has no dry run, so the estimate is every repository
/// folder in the store, and <c>gc</c> keeps those a project still uses.</para>
/// </summary>
public sealed partial class PreCommitStoreProvider : CleanupProviderBase
{
    /// <summary>Set by the user to move the whole store.</summary>
    public const string HomeVariable = "PRE_COMMIT_HOME";

    /// <summary>Read by pre-commit on every platform where <see cref="HomeVariable"/> is unset.</summary>
    public const string XdgCacheVariable = "XDG_CACHE_HOME";

    /// <summary>The folder pre-commit keeps inside a cache home.</summary>
    private const string StoreFolder = "pre-commit";

    /// <summary>
    /// The files pre-commit itself writes at the top of the store, which no run may take: its record of
    /// repositories and configurations, the lock that serialises it, and the note saying whose folder
    /// it is.
    /// </summary>
    public static readonly IReadOnlyList<string> StoreFiles = ["db.db", ".lock", "README"];

    /// <summary>
    /// The pre-commit launcher a pip or pipx install puts on the PATH, which stays running while it
    /// does. A run started as <c>python -m pre_commit</c> is not seen here, and does not need to be:
    /// <c>gc</c> waits for the store's lock.
    /// </summary>
    private static readonly IReadOnlyList<string> PreCommitProcessNames = ["pre-commit"];

    private readonly ISystemDirectories _system;

    public PreCommitStoreProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "pre-commit";

    public override string Name => "pre-commit hook environments";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Hook environments no project uses any more are removed. A project whose configuration pre-commit "
        + "can no longer read, such as one on a drive that is not connected, installs its hooks again the "
        + "next time pre-commit runs there. Stashed unstaged changes are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "pre-commit, a framework that runs checks before each Git commit",
        Publisher = "the pre-commit project",
        Purpose = "pre-commit clones every hook repository a project's configuration names and installs "
            + "an environment for each version, and keeps all of them after the projects move on.",
        Recommendation = "Deguffer runs 'pre-commit gc', which removes only the hook repositories no "
            + "project still uses, and never 'pre-commit clean', which also deletes the unstaged changes "
            + "pre-commit stashed during a commit.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => PreCommitProcessNames;

    /// <summary>Where pre-commit keeps its store when neither variable is set.</summary>
    public string DefaultStore => Path.Combine(Environment.UserProfile, ".cache", StoreFolder);

    /// <summary>
    /// The store <c>pre-commit gc</c> acts on, or null where the setting it resolves from names nothing
    /// this can examine as pre-commit's.
    /// </summary>
    public string? ResolveStore() => Resolve().Folder;

    /// <summary>
    /// The whole store, recognising nothing: a repository folder removed by path leaves its row in
    /// <c>db.db</c>, and every other entry is pre-commit's record or the user's stashed work.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        ResolveStore() is { } store
            ?
            [
                new ToolRoot(
                    store,
                    "This is pre-commit's store. Deguffer clears it only with 'pre-commit gc', because a hook "
                    + "folder removed by hand breaks every project that uses it, and the store also holds "
                    + "unstaged changes pre-commit stashed during a commit.",
                    static _ => false),
            ]
            : [];

    /// <summary>
    /// A folder a variable names but this declines is still where pre-commit keeps its record and the
    /// changes it stashed, so Explore refuses those there and nothing else. See <see cref="ToolRoot.Sparing"/>.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>(
            Resolve().DeclinedFolder is { } declined
                ?
                [
                    ToolRoot.Sparing(
                        declined,
                        "pre-commit keeps its record of hook repositories here, so Deguffer leaves it alone.",
                        StoreFiles),
                ]
                : []);

    /// <summary>
    /// The store <c>gc</c> is sent to and the default one, because a variable can change after a
    /// duplicate search has read it. Whole, because <c>gc</c> removes whichever repository folders
    /// <c>db.db</c> names, and Python's random suffix gives them no name to list.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. new[] { ResolveStore(), DefaultStore }
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(CleanedPlace.Whole),
        ]);

    /// <summary>
    /// A repository folder in the store, never the store existing: a store holding only stashed changes
    /// has nothing <c>gc</c> could remove. Not the command, so a store whose pre-commit is not on the
    /// PATH is still reported.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveStore() is { } store
            && !LongPath.IsReparsePoint(store)
            && FolderEntries.Of(store) is { } entries
            && entries.Any(IsRepository));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var setting = Resolve();

        if (setting.Folder is not { } store)
        {
            return EmptyPlan(
                $"{SettingVariable} is set to '{setting.Value}', and Deguffer will not treat the folder it names "
                + $"as pre-commit's store: {setting.Declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(store, $"pre-commit has not installed any hooks for this user — no {store} directory.") is { } nothing)
        {
            return nothing;
        }

        if (LongPath.IsReparsePoint(store))
        {
            return UnexaminedPlan(
                $"Leaving '{store}' alone: it is a link to somewhere else, and Deguffer does not look through a link.");
        }

        if (FolderEntries.Of(store) is not { } entries)
        {
            return UnreadableRootPlan(store);
        }

        var repositories = entries
            .Where(IsRepository)
            .Select(entry => LongPath.Display(entry.FullName))
            .ToList();

        if (repositories.Count == 0)
        {
            return EmptyPlan($"pre-commit has no hook environments in {store}.");
        }

        if (Environment.FindExecutable("pre-commit") is not { } preCommit)
        {
            return UnexaminedPlan(
                $"pre-commit's store at {store} holds {repositories.Count} hook "
                + $"{(repositories.Count == 1 ? "repository" : "repositories")}, but no 'pre-commit' command is on "
                + "the PATH, and only 'pre-commit gc' can tell which of them no project uses any more. "
                + "Deguffer is leaving them alone.");
        }

        var measured = await MeasureAllAsync(repositories, ct).ConfigureAwait(false);
        var stashed = entries.Count(entry => !IsFolder(entry) && StashedChanges().IsMatch(entry.Name));
        var notes = new List<PlanNote>
        {
            new(
                PlanNoteSeverity.Information,
                $"pre-commit keeps its hook environments in {store}. The figure is all of them: "
                + "'pre-commit gc' keeps those a project still uses, and says nothing beforehand."),
        };

        if (stashed > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"The store also holds {stashed} {(stashed == 1 ? "copy" : "copies")} of unstaged changes "
                + "pre-commit stashed during a commit. They are left alone, because one can be the only copy of that work."));
        }

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
            Steps =
            [
                // Never "clean", which deletes the whole store and the stashed changes in it.
                new RunCommandStep(preCommit, "gc", "Remove the hook environments no project uses, using pre-commit's own command")
                {
                    // gc commits its database only after its last removal, so one ended part-way leaves rows
                    // naming folders already half removed, and every project using them fails. Left to finish.
                    WhenCancelled = CancelledCommand.Finishes,
                    Estimated = measured.Total with { IsCeiling = true },
                    MeasuredPaths = repositories,
                },
            ],
            ProtectedPaths = Protect(
            [
                (store, "pre-commit's store itself must survive — 'pre-commit gc' removes only hook repositories inside it."),
                .. StoreFiles.Select(name => (Path.Combine(store, name), "pre-commit's own record of its store, which 'pre-commit gc' keeps.")),
                .. entries
                    .Where(entry => !IsRepository(entry))
                    .Select(entry => (LongPath.Display(entry.FullName), WhySurvives(entry))),
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// A folder <c>tempfile.mkdtemp(prefix='repo')</c> made: <c>repo</c> and Python's eight random
    /// characters. A link carrying the name is not one, because pre-commit never makes one.
    /// </summary>
    private static bool IsRepository(FileSystemInfo entry) =>
        IsFolder(entry)
        && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
        && RepositoryFolder().IsMatch(entry.Name);

    private static bool IsFolder(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.Directory);

    private static string WhySurvives(FileSystemInfo entry) =>
        !IsFolder(entry) && StashedChanges().IsMatch(entry.Name)
            ? "Unstaged changes pre-commit stashed during a commit, which can be the only copy of that work."
            : "Not something 'pre-commit gc' removes, so it must survive.";

    /// <summary>
    /// The store as <c>_get_default_directory</c> resolves it: <see cref="HomeVariable"/>, then
    /// <see cref="XdgCacheVariable"/> joined with <c>pre-commit</c>, then <see cref="DefaultStore"/>.
    /// For the second, the <c>pre-commit</c> folder inside is what <see cref="ConfiguredFolder"/> judges,
    /// because a cache home is shared by many tools.
    /// </summary>
    private ConfiguredFolder.Setting Resolve() =>
        SettingVariable == HomeVariable
            ? ConfiguredFolder.FromToolVariable(HomeVariable, DefaultStore, Environment, _system, Volumes)
            : ConfiguredFolder.FromToolVariable(XdgCacheVariable, DefaultStore, Environment, _system, Volumes, within: StoreFolder);

    /// <summary>The variable pre-commit reads its store from: <see cref="HomeVariable"/> wherever it is set.</summary>
    private string SettingVariable =>
        string.IsNullOrWhiteSpace(Environment.GetToolEnvironmentVariable(HomeVariable)) ? XdgCacheVariable : HomeVariable;

    // Python's tempfile draws its suffix from lowercase letters, digits and the underscore.
    [GeneratedRegex("^repo[a-z0-9_]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryFolder();

    // staged_files_only.py: f'patch{int(time.time())}-{os.getpid()}'.
    [GeneratedRegex("^patch[0-9]+-[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StashedChanges();
}
