using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Deno's cache folder, <c>DENO_DIR</c>: remote modules, npm packages, compiled output and the
/// databases Deno keeps to skip work it has done before. Researched from Deno's source at
/// <c>e7b39ebd</c> (October 2026) rather than measured, because no Deno was installed on the machine
/// this was written against.
///
/// <para><b>Where it is.</b> <c>resolve_deno_dir</c> in <c>libs/cache_dir/deno_dir.rs</c> takes
/// <c>DENO_DIR</c>, then <c>XDG_CACHE_HOME\deno</c>, which it honours on Windows too, then the
/// user's cache folder joined with <c>deno</c>, which <c>sys_traits</c> reads as
/// <c>FOLDERID_LocalAppData</c>: so <c>%LOCALAPPDATA%\deno</c>, as Deno's installation page states.
/// An empty variable counts as unset, and a relative one is joined to the working directory of the
/// process reading it, which Deguffer is not, so a relative value is declined.</para>
///
/// <para><b>A recorded exception to §5.1.</b> <c>deno clean</c> deletes the whole folder
/// (<c>cli/tools/clean.rs</c>: <c>rm_rf(&amp;deno_dir.root)</c>), and the folder holds the user's data:
/// <c>location_data</c> is each program's <c>localStorage</c>, its default <c>Deno.openKv()</c> database
/// and its Cache API store, and <c>deno_history.txt</c> is the REPL history. <c>--except</c> keeps the
/// files at the top level but still deletes the folders inside <c>location_data</c>. The tool's own
/// command destroys user data in both forms, so this provider removes the recognised children by path
/// instead, and names those two as survivors.</para>
///
/// <para><b>What is recognised is what <c>DenoDir</c> defines.</b> Every name below is a function of
/// <c>libs/resolver/cache/deno_dir.rs</c>. Deno writes other folders into the root too
/// (<c>node_compat_bin</c>, <c>tsc</c>, <c>task_cache_v1</c> and others, each from its own tool), and
/// those are not recognised, so they stay Tier 4.</para>
///
/// <para><b>Each database goes with its write-ahead log.</b> Deno opens every cache database in WAL
/// mode on Windows (<c>cli/cache/cache_db.rs</c>), so SQLite keeps <c>&lt;name&gt;-wal</c> and
/// <c>&lt;name&gt;-shm</c> beside it. They are offered with the database, never instead of it.</para>
/// </summary>
public sealed class DenoCacheProvider : CleanupProviderBase
{
    /// <summary>Set by the user to move the whole folder.</summary>
    public const string DirectoryVariable = "DENO_DIR";

    /// <summary>Honoured by Deno on every platform where <see cref="DirectoryVariable"/> is unset.</summary>
    public const string XdgCacheVariable = "XDG_CACHE_HOME";

    /// <summary>The folder Deno's language server keeps open while an editor runs it, and every other Deno command too.</summary>
    private static readonly IReadOnlyList<string> DenoProcessNames = ["deno"];

    /// <summary>
    /// The databases <c>DenoDir</c> names, each at the version Deno reads now. A database at an older
    /// version is never read again, and is not recognised here either, because its name is not one
    /// this source states.
    /// </summary>
    public static readonly IReadOnlyList<string> Databases =
    [
        "dep_analysis_cache_v2",
        "fast_check_cache_v2",
        "node_analysis_cache_v2",
        "check_cache_v2",
        "v8_code_cache_v2",
        "lint_incremental_cache_v2",
        "fmt_incremental_cache_v2",
    ];

    /// <summary>The suffixes SQLite gives the two files it keeps beside a database in WAL mode.</summary>
    private static readonly IReadOnlyList<string> WriteAheadSuffixes = ["-wal", "-shm"];

    /// <summary>
    /// The folders <c>DenoDir</c> names. <c>location_data</c> is declared at Tier 4, so the reason the
    /// user reads is what it holds rather than "not recognised".
    /// </summary>
    public static readonly DisposableChildSet Folders = new(
    [
        new ChildClassification(
            "remote",
            SafetyTier.RegenerableCache,
            "Modules Deno downloaded from web addresses. Deno downloads each again the next time a program imports it."),
        new ChildClassification(
            "npm",
            SafetyTier.RegenerableCache,
            "npm packages Deno downloaded. Deno downloads each again the next time a program needs it."),
        new ChildClassification(
            "gen",
            SafetyTier.RegenerableCache,
            "Deno's compiled output for the TypeScript it ran. Deno compiles it again."),
        new ChildClassification(
            "registries",
            SafetyTier.RegenerableCache,
            "What Deno's language server read from module registries to offer completions. It reads them again."),
        new ChildClassification(
            "dl",
            SafetyTier.RegenerableCache,
            "Deno releases 'deno upgrade' and 'deno compile' downloaded. Each is downloaded again when it is next needed."),
        new ChildClassification(
            "location_data",
            SafetyTier.DoNotTouch,
            "Data Deno programs saved: their localStorage, their default key-value database and what they "
            + "stored with the Cache API. Nothing downloads it again."),
    ]);

    /// <summary>The files <c>DenoDir</c> names: each database and its log, and the upgrade check.</summary>
    public static readonly DisposableChildSet Files = new(
    [
        .. Databases.SelectMany(database => new[] { database }.Concat(WriteAheadSuffixes.Select(suffix => database + suffix)))
            .Select(name => new ChildClassification(
                name,
                SafetyTier.RegenerableCache,
                "One of Deno's records of work it has already done, so it can skip it. Deno starts the record again.")),
        new ChildClassification(
            "latest.txt",
            SafetyTier.RegenerableCache,
            "When Deno last checked for a new release. It checks again."),
        new ChildClassification(
            "deno_history.txt",
            SafetyTier.DoNotTouch,
            "The commands typed into Deno's interactive shell. Nothing writes it again."),
    ]);

    private const string LinkReason = "A link rather than a directory or a file, so what it points at was never classified.";

    private readonly ISystemDirectories _system;

    public DenoCacheProvider(
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

    public override string Id => "deno";

    public override string Name => "Deno cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next Deno program you run downloads the modules and packages it imports again and "
        + "type-checks from scratch, so it starts more slowly once. What Deno programs saved and your "
        + "REPL history are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Deno, a JavaScript and TypeScript runtime",
        Publisher = "Deno Land Inc.",
        Purpose = "Deno keeps every module and npm package a program imported, its compiled output and "
            + "records of the work it has done in one folder, and never removes any of it.",
        Recommendation = "Deguffer removes what Deno downloads or works out again, one item at a time, "
            + "rather than with 'deno clean', which also deletes the data Deno programs saved and your "
            + "REPL history.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => DenoProcessNames;

    /// <summary>Where Deno keeps its folder when neither variable is set.</summary>
    public string DefaultRoot => Path.Combine(Environment.LocalAppData, "deno");

    /// <summary>
    /// The folder Deno resolves, or null where the setting it resolves from names nothing this can
    /// examine as Deno's.
    /// </summary>
    public string? ResolveRoot() => Resolve().Folder;

    public override IReadOnlyList<ToolRoot> ToolRoots =>
        ResolveRoot() is { } root
            ?
            [
                new ToolRoot(
                    root,
                    "This is Deno's cache folder. Deguffer removes what Deno downloads or works out again, and "
                    + "leaves what Deno programs saved and the REPL history.",
                    child => child.Kind switch
                    {
                        ChildKind.Folder => Folders.IsDisposable(child.Name),
                        ChildKind.File => Files.IsDisposable(child.Name),
                        _ => false,
                    }),
            ]
            : [];

    /// <summary>
    /// A folder a variable names but this declines is still where Deno keeps what its programs saved
    /// and the REPL history, so Explore refuses those there and nothing else. Declared here rather
    /// than in <see cref="ToolRoots"/> because a declaration made here only ever adds a refusal. See
    /// <see cref="ToolRoot.Sparing"/>.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>(
            Resolve().DeclinedFolder is { } declined
                ?
                [
                    ToolRoot.Sparing(
                        declined,
                        "Deno keeps what its programs saved and your REPL history here, so Deguffer leaves those alone.",
                        [.. Folders.KeptNames, .. Files.KeptNames]),
                ]
                : []);

    /// <summary>
    /// The recognised children of the folder in use and of the default one, because a variable can be
    /// unset again after a duplicate search has read it. Never <c>location_data</c>.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. new[] { ResolveRoot(), DefaultRoot }
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(root => Folders.DisposableNames.Concat(Files.DisposableNames)
                    .Select(child => CleanedPlace.Whole(Path.Combine(root, child)))),
        ]);

    /// <summary>
    /// A disposable child actually on disk, never the root existing: a folder holding only what Deno
    /// programs saved has nothing this row would offer.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveRoot() is { } root
            && (Folders.DisposableNames.Any(name => LongPath.DirectoryMayExist(Path.Combine(root, name)))
                || Files.DisposableNames.Any(name => LongPath.FileMayExist(Path.Combine(root, name)))));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var setting = Resolve();

        if (setting.Folder is not { } root)
        {
            return EmptyPlan(
                $"{SettingVariable} is set to '{setting.Value}', and Deguffer will not treat the folder it names "
                + $"as Deno's: {setting.Declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(root, $"Deno has not cached anything for this user — no {root} directory.", out var rootIsLink) is { } nothing)
        {
            return nothing;
        }

        if (rootIsLink)
        {
            return UnexaminedPlan(
                $"Leaving '{root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        if (FolderEntries.Of(root) is not { } entries)
        {
            return UnreadableRootPlan(root);
        }

        var notes = new List<PlanNote>();
        var declined = new List<(string Path, string Reason)>();
        var targets = new List<DeletionTarget>();

        // Deno itself holds these databases open, and its language server runs for as long as an
        // editor does, so every step is asked again just before it runs.
        var stillUnused = new RunningProcessCheck(Inspector, DenoProcessNames);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);
            var isFolder = entry.Attributes.HasFlag(FileAttributes.Directory);

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Decline(path, entry.Name, LinkReason);
                continue;
            }

            var classification = (isFolder ? Folders : Files).Classify(entry.Name);

            if (!classification.Tier.IsOfferable())
            {
                Decline(path, entry.Name, classification.Reason);
                continue;
            }

            targets.Add(new DeletionTarget(
                path,
                classification.Reason,
                Kind: isFolder ? TargetKind.Directory : TargetKind.File,
                UseCheck: stillUnused));
        }

        if (targets.Count == 0 && declined.Count == 0)
        {
            return EmptyPlan($"Deno has cached nothing in {root} yet.");
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
            ProtectedPaths = await ProtectAsync(
                [
                    (root, "Deno's folder itself must survive — only what Deno downloads or works out again is removed."),
                    .. Survivors(root),
                    .. declined,
                ],
                ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            WasNotExamined = targets.Count == 0 && declined.Count > 0,
        };

        void Decline(string path, string name, string reason)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{name}' alone: {reason}"));
            declined.Add((path, reason));
        }
    }

    /// <summary>
    /// §5.6's named survivors, asserted whether or not the listing met them, so a rule that ever took
    /// one of them fails the run rather than passing it.
    /// </summary>
    private static IEnumerable<(string Path, string Reason)> Survivors(string root) =>
        Folders.KeptNames.Select(name => (Path.Combine(root, name), Folders.Classify(name).Reason))
            .Concat(Files.KeptNames.Select(name => (Path.Combine(root, name), Files.Classify(name).Reason)));

    /// <summary>
    /// The folder as <c>resolve_deno_dir</c> resolves it: <see cref="DirectoryVariable"/>, then
    /// <see cref="XdgCacheVariable"/> joined with <c>deno</c>, then <see cref="DefaultRoot"/>. For the
    /// second, the <c>deno</c> folder inside is what <see cref="ConfiguredFolder"/> judges: a cache
    /// home is often the profile's own <c>.cache</c>, and only Deno's folder in it is Deno's.
    /// </summary>
    private ConfiguredFolder.Setting Resolve() =>
        SettingVariable == DirectoryVariable
            ? ConfiguredFolder.FromVariable(DirectoryVariable, DefaultRoot, Environment, _system, Volumes)
            : ConfiguredFolder.FromVariable(XdgCacheVariable, DefaultRoot, Environment, _system, Volumes, within: "deno");

    /// <summary>The variable Deno reads its folder from: <see cref="DirectoryVariable"/> wherever it is set.</summary>
    private string SettingVariable =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DirectoryVariable)) ? XdgCacheVariable : DirectoryVariable;
}
