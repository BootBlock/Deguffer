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

    public override ProviderBasis Basis => ProviderBasis.Researched;

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
    public string? ResolveStore() => Resolve(VariableReading.Tool).Folder;

    /// <summary>
    /// The whole store, recognising nothing: a repository folder removed by path leaves its row in
    /// <c>db.db</c>, and every other entry is pre-commit's record or the user's stashed work. Every store
    /// either reading of the variables names, because a pre-commit the user starts next reads the
    /// machine as it now stands.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. Readings()
            .Select(setting => setting.Folder)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(store => new ToolRoot(
                store,
                "This is pre-commit's store. Deguffer clears it only with 'pre-commit gc', because a hook "
                + "folder removed by hand breaks every project that uses it, and the store also holds "
                + "unstaged changes pre-commit stashed during a commit.",
                static _ => false)),
    ];

    /// <summary>
    /// A folder either reading of a variable names but this declines is still where pre-commit keeps its
    /// record, its hook repositories and the changes it stashed, so Explore refuses those there and
    /// nothing else. Built as <see cref="ToolRoot.Sparing"/> builds its root, with a predicate rather than
    /// a list, because a hook folder and a stashed change are known by their pattern, never by a name
    /// given in advance.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. Readings()
                .Select(setting => setting.DeclinedFolder)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(declined => new ToolRoot(
                    declined,
                    "pre-commit keeps its hook repositories, its record of them and the unstaged changes it "
                    + "stashed during a commit here, so Deguffer leaves those alone.",
                    static child => !IsStoreEntry(child.Name),
                    ToolRootClaim.NamedEntries)),
        ]);

    /// <summary>
    /// The store either reading of the variables names and the default one, because a variable can change
    /// after a duplicate search has read it. Whole, because <c>gc</c> removes whichever repository folders
    /// <c>db.db</c> names, and Python's random suffix gives them no name to list.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. Readings()
                .Select(setting => setting.Folder)
                .Append(DefaultStore)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(CleanedPlace.Whole),
        ]);

    /// <summary>
    /// A repository folder in the store, never the store existing: a store holding only stashed changes
    /// has nothing <c>gc</c> could remove. Not the command, so a store whose pre-commit is not on the
    /// PATH is still reported. A store Windows would not describe or list, and a store that is a link,
    /// count as present, because only the plan can say that it left them alone.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveStore() is { } store && LongPath.ProbeDirectory(store, out var storeIsLink) switch
        {
            PathPresence.Absent => false,
            PathPresence.Refused => true,
            _ => storeIsLink is true || FolderEntries.Of(store) is not { } entries || entries.Any(IsRepository),
        });

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var setting = Resolve(VariableReading.Tool);

        if (setting.Folder is not { } store)
        {
            return EmptyPlan(
                $"{SettingVariable(VariableReading.Tool)} is set to '{setting.Value}', and Deguffer will not treat the folder it names "
                + $"as pre-commit's store: {setting.Declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(store, $"pre-commit has not installed any hooks for this user — no {store} directory.", out var storeIsLink) is { } nothing)
        {
            return nothing;
        }

        if (storeIsLink)
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
    /// The store as <c>_get_default_directory</c> resolves it from the variables as <paramref name="reading"/>
    /// reads them: <see cref="HomeVariable"/>, then <see cref="XdgCacheVariable"/> joined with
    /// <c>pre-commit</c>, then <see cref="DefaultStore"/>. For the second, the <c>pre-commit</c> folder
    /// inside is what <see cref="ConfiguredFolder"/> judges, because a cache home is shared by many tools.
    /// The plan reads them as <c>gc</c> does, from the environment Deguffer hands it.
    /// </summary>
    private ConfiguredFolder.Setting Resolve(VariableReading reading)
    {
        var variable = SettingVariable(reading);

        // Python's "or" keeps a value of spaces, and pre-commit resolves it against its own working
        // directory, so it is a relative value rather than an unset one, which ConfiguredFolder's trim
        // would read it as.
        if (ConfiguredFolder.Read(reading, variable, Environment) is { Length: > 0 } value && string.IsNullOrWhiteSpace(value))
        {
            return new ConfiguredFolder.Setting(value, null, null, ConfiguredFolder.NotAFullPath);
        }

        return variable == HomeVariable
            ? ConfiguredFolder.FromVariable(reading, HomeVariable, DefaultStore, Environment, _system, Volumes)
            : ConfiguredFolder.FromVariable(reading, XdgCacheVariable, DefaultStore, Environment, _system, Volumes, within: StoreFolder);
    }

    /// <summary>
    /// The store as both readings resolve it, for what Explore and a duplicate search refuse: <c>gc</c>
    /// acts on the first, and a pre-commit the user starts next acts on the second.
    /// </summary>
    private IReadOnlyList<ConfiguredFolder.Setting> Readings() =>
        [Resolve(VariableReading.Tool), Resolve(VariableReading.Current)];

    /// <summary>
    /// The variable pre-commit reads its store from: <see cref="HomeVariable"/> wherever it holds anything,
    /// spaces included, as Python's <c>or</c> decides.
    /// </summary>
    private string SettingVariable(VariableReading reading) =>
        string.IsNullOrEmpty(ConfiguredFolder.Read(reading, HomeVariable, Environment)) ? XdgCacheVariable : HomeVariable;

    /// <summary>
    /// A name pre-commit gives something in its store: its own files, a hook repository folder or a
    /// stashed change. Asked of a folder's children by name alone, so a file carrying a folder's
    /// pattern is refused too, which is the safe direction.
    /// </summary>
    private static bool IsStoreEntry(string name) =>
        StoreFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
        || RepositoryFolder().IsMatch(name)
        || StashedChanges().IsMatch(name);

    // Python's tempfile draws its suffix from lowercase letters, digits and the underscore.
    [GeneratedRegex("^repo[a-z0-9_]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryFolder();

    // staged_files_only.py: f'patch{int(time.time())}-{os.getpid()}'.
    [GeneratedRegex("^patch[0-9]+-[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StashedChanges();
}
