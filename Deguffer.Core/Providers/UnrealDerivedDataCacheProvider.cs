using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Unreal Engine derived-data cache every project on the machine shares: compiled shaders and
/// prepared asset data, in the filesystem cache of Unreal Engine 5.3 and earlier and in the Zen
/// stores of 5.1 and later. <see cref="UnrealCacheLocations"/> records where each one is.
///
/// <para><b>Tier 2.</b> Everything in it is derived from project content, so nothing is lost, but
/// the refill is a shader compile measured in tens of minutes on a real project, which is §3's
/// definition of the second tier. Community reports put an active project's cache at 16 to 100 GB,
/// growing by 1.5 to 2 GB each working day. Those are community figures, not Epic's.</para>
///
/// <para><b>§5.1 has nothing to prefer.</b> Epic documents no command that clears either cache. The
/// filesystem cache expires unused files itself, Zen collects what nothing refers to, and Epic's own
/// advice for clearing one by hand is to delete the data folder and let it fill again. The engine
/// removes an old default store the same way, whole.</para>
///
/// <para><b>§5.2.</b> <c>Common</c> and <c>Zen</c> are never targets. Only
/// <c>Common\DerivedDataCache</c> and each store are named, nothing is enumerated to find a target,
/// and <see cref="ToolRoots"/> tells Explore the same. Every engine version's folder beside
/// <c>Common</c>, which holds that version's settings and crash reports, is asserted to survive, as
/// is the Zen server's own installation beside each default store.</para>
///
/// <para><b>Presence is content, never a folder.</b> <c>%LOCALAPPDATA%\UnrealEngine</c> exists on
/// every machine that has ever run the Epic launcher, and was measured at 1 MB of settings with no
/// cache in it at all. A cache folder that holds nothing is no row either, rather than a row
/// promising nothing.</para>
///
/// <para><b>A running Zen server holds every store back (§5.3).</b> <c>zenserver</c> starts with
/// the editor, and can be set to keep running after it closes. Removing a store's data under the
/// server writing it is not provably safe, so while one runs every store is left alone, named as a
/// survivor, and refused in Explore. The filesystem cache has no server: the editor holding files
/// open there is a warning, and anything held open stays.</para>
///
/// <para><b>No administrator rights.</b> <c>%PROGRAMDATA%\Epic</c> carries the write access Epic's
/// installer grants every user, which <see cref="EpicLauncherContentCacheProvider"/> measured. On a
/// machine where it does not, the removal fails loudly, reclaims nothing, and leaves §5.6 passing,
/// which is the direction to be wrong in.</para>
/// </summary>
public sealed class UnrealDerivedDataCacheProvider : CleanupProviderBase
{
    /// <summary>The Zen storage server's process name.</summary>
    private const string ZenServer = "zenserver";

    private const string HeldReason =
        "Unreal's Zen server is running, and removing a store's data under the server using it is "
        + "not safe. Close the Unreal Editor, and stop Zen if it is set to keep running, then scan "
        + "again.";

    private const string StoreReason =
        "A Zen store's derived data. Unreal fills it again the next time a project opens.";

    private static readonly string LegacyCache = Path.Combine("Common", "DerivedDataCache");

    private readonly ISystemDirectories _system;

    public UnrealDerivedDataCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "unreal-ddc";

    public override string Name => "Unreal Engine derived data cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next time you open a project, Unreal compiles its shaders and prepares its assets "
        + "again. On a large project that can take tens of minutes. Your projects, your engine "
        + "settings and your installed engines are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Unreal Editor",
        Publisher = "Epic Games",
        Purpose = "Unreal keeps the compiled shaders and prepared forms of every project's assets "
            + "in one cache shared by all of them, so the editor does not have to produce them "
            + "again each time a project opens. Recent versions keep it in a store called Zen, and "
            + "earlier ones in a folder of their own.",
        Recommendation = "Epic's own advice is to delete the cache folder and let it fill again. "
            + "The refill is a shader compile, which on a large project takes a long time.",
    };

    /// <summary>
    /// §5.3's warning names the editor. The Zen server is not named here, because while it runs the
    /// stores are held back rather than warned about, and the plan says so in its own words.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => UnrealProjectLayout.EditorProcessNames;

    /// <summary>
    /// §5.2 for Explore. Unreal's own folder recognises nothing, so every engine version's settings
    /// are refused. <c>Common</c> recognises only the filesystem cache, and each default <c>Zen</c>
    /// folder only its data, so the server installed beside it is refused too.
    ///
    /// <para>A folder a setting names is declared nowhere here. It is somebody's own folder, which a
    /// root that recognised only Zen's store would refuse everything else in, and it may be a
    /// volume's root.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        new ToolRoot(
            UnrealCacheLocations.EngineRoot(Environment),
            "This is Unreal Engine's own folder, where every engine version keeps its settings. "
            + "Deguffer removes only the shared cache inside it.",
            static _ => false),
        ToolRoot.Of(
            UnrealCacheLocations.Common(Environment),
            "This is the folder Unreal Engine versions share. Deguffer removes only the derived "
            + "data cache and the Zen store inside it, never the Zen server installed there.",
            new DisposableChildSet(
            [
                new ChildClassification("DerivedDataCache", Tier, "Unreal's shared derived data cache."),
            ])),
        .. UnrealCacheLocations.DefaultStores(Environment, _system).Select(store => ToolRoot.Of(
            Path.GetDirectoryName(store.Path)!,
            "This is Unreal's Zen folder. Deguffer removes only the store's data inside it, and "
            + "never the server installed beside it.",
            new DisposableChildSet(
            [
                new ChildClassification(Path.GetFileName(store.Path), Tier, StoreReason),
            ]))),
    ];

    /// <summary>
    /// Every store that is there, as a root recognising nothing, while a Zen server runs. A
    /// declaration that ages, which is why it is here rather than in <see cref="ToolRoots"/>.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>(
            ZenServerIsRunning()
                ? [.. Stores().Select(store => store.Path).Where(LongPath.DirectoryMayExist)
                    .Select(store => new ToolRoot(store, HeldReason, static _ => false))]
                : []);

    /// <summary>
    /// Any cache with something in it, or any that Windows would not describe. A refusal reads as
    /// presence, so the plan can say what it could not reach rather than the row never appearing.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Stores()
            .Select(store => store.Path)
            .Prepend(Path.Combine(UnrealCacheLocations.EngineRoot(Environment), LegacyCache))
            .Any(path => LongPath.ProbeDirectory(path) switch
            {
                PathPresence.Refused => true,
                PathPresence.Present => !HoldsNothing(path),
                _ => false,
            }));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var stores = Stores();
        var scan = DeclaredLocations.Examine(Declare(stores), ct);

        var storePaths = new HashSet<string>(stores.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        var zenRunning = ZenServerIsRunning();

        var targets = new List<DeletionTarget>();
        var held = new List<string>();

        foreach (var target in scan.Targets)
        {
            ct.ThrowIfCancellationRequested();

            if (HoldsNothing(target.Path))
            {
                continue;
            }

            if (zenRunning && storePaths.Contains(target.Path))
            {
                held.Add(target.Path);
                continue;
            }

            targets.Add(target);
        }

        if (targets.Count == 0 && held.Count == 0 && scan.Declined.Count == 0 && !scan.CouldNotBeReached)
        {
            return EmptyPlan("No Unreal Engine derived data cache holds anything for this user.");
        }

        var notes = new List<PlanNote>(scan.Notes);

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                $"Left Unreal's Zen {(held.Count == 1 ? "store" : "stores")} alone: {ZenServer} is "
                + "running, and removing a store's data under the server using it is not safe. Close "
                + "the Unreal Editor, and stop Zen if it is set to keep running, then scan again to "
                + $"include {(held.Count == 1 ? "it" : "them")}."));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3, and only where something is going to be removed.
        if (steps.Count > 0 && BuildRunningProcessNote() is { } warning)
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
            ProtectedPaths = Protect([.. scan.Protected, .. held.Select(store => (store, HeldReason))]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = scan.CouldNotBeReached,

            // A store held back and a cache behind a link are both something real left unexamined,
            // so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || scan.Declined.Count > 0),
        };
    }

    /// <summary>
    /// The locations, root by root, with what §5.6 asserts beside each.
    ///
    /// <para><c>%PROGRAMDATA%\Epic</c> is a root rather than its <c>Zen</c> folder, so the Epic
    /// launcher's own folders beside it are named survivors rather than things this provider never
    /// mentions. A store a setting names is a root of its own, and the folder holding it survives.</para>
    /// </summary>
    private IReadOnlyList<DeclaredRoot> Declare(IReadOnlyList<ZenStoreLocation> stores)
    {
        var engine = UnrealCacheLocations.EngineRoot(Environment);
        var programData = Path.Combine(_system.ProgramData, "Epic");

        return
        [
            new DeclaredRoot(
                engine,
                "Unreal Engine's own folder must survive. Every engine version keeps its settings in "
                + "it, and only the shared cache inside is removed.",
                RequiresElevation: false,
                [
                    new DeclaredLocation(
                        LegacyCache,
                        "The derived data cache Unreal Engine 5.3 and earlier share. Unreal fills it "
                        + "again the next time a project opens.",
                        ReportsAge: false),
                    .. Locations(stores, engine),
                ],
                [
                    (Path.Combine("Common", "Zen", "Install"),
                        "The Zen server Unreal Engine 5.4 and later start with the editor."),
                    .. EngineVersionFolders(engine),
                ]),
            new DeclaredRoot(
                programData,
                "Epic's machine-wide folder must survive. Only a Zen store's data inside it is "
                + "removed.",
                RequiresElevation: false,
                Locations(stores, programData),
                [
                    (Path.Combine("Zen", "Install"),
                        "The Zen server Unreal Engine 5.1 to 5.3 start with the editor."),
                    ("EpicGamesLauncher", "The Epic Games launcher's machine-wide data."),
                    (Path.Combine("UnrealEngineLauncher", "LauncherInstalled.dat"),
                        "The machine's record of where its Epic games and engines are installed."),
                    ("EpicOnlineServices", "The services Epic games sign in and play online through."),
                ]),
            .. stores
                .Where(store => store.IsConfigured)
                .Select(store => new DeclaredRoot(
                    store.Root,
                    "The folder a setting names for Unreal's Zen store must survive. Only the store "
                    + "inside it is removed.",
                    RequiresElevation: false,
                    [new DeclaredLocation(store.RelativePath, StoreReason, ReportsAge: false)],
                    [])),
        ];
    }

    /// <summary>The default stores whose root is <paramref name="root"/>, as locations under it.</summary>
    private static IReadOnlyList<DeclaredLocation> Locations(IReadOnlyList<ZenStoreLocation> stores, string root) =>
    [
        .. stores
            .Where(store => !store.IsConfigured && store.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
            .Select(store => new DeclaredLocation(store.RelativePath, StoreReason, ReportsAge: false)),
    ];

    /// <summary>
    /// Every Zen store this provider may reach: the two defaults, and each store a setting names that
    /// Zen marked as its own.
    ///
    /// <para>A named store is dropped where it would take a default store or a server's
    /// installation with it, or sits inside a default store that is reached anyway. A local cache
    /// path set to <c>Common</c> would otherwise make <c>Common\Zen</c> a store, and its
    /// <c>Install</c> folder with it.</para>
    /// </summary>
    private IReadOnlyList<ZenStoreLocation> Stores()
    {
        var defaults = UnrealCacheLocations.DefaultStores(Environment, _system);

        IReadOnlyList<string> spared =
        [
            UnrealCacheLocations.EngineRoot(Environment),
            Path.Combine(_system.ProgramData, "Epic"),
            .. defaults.Select(store => store.Path),
            .. defaults.Select(store => Path.Combine(Path.GetDirectoryName(store.Path)!, "Install")),
        ];

        return
        [
            .. defaults,
            .. UnrealCacheLocations.ConfiguredStores(Environment).Where(store =>
                !spared.Any(path => LongPath.Contains(store.Path, path))
                && !defaults.Any(d => LongPath.Contains(d.Path, store.Path))

                // A classification, so a refusal is not evidence for it: only a marker that is
                // there makes the folder a store.
                && LongPath.ProbeFile(Path.Combine(store.Path, UnrealCacheLocations.StoreMarker))
                    is PathPresence.Present),
        ];
    }

    /// <summary>
    /// Every engine version's folder beside <c>Common</c>, and the settings inside it, for §5.6.
    ///
    /// <para>Listed, which a declared root otherwise never is, because the version names are the
    /// user's installed engines and cannot be written down in advance. The listing names survivors
    /// and nothing else: no entry found here can become a target.</para>
    /// </summary>
    private static IEnumerable<(string RelativePath, string Reason)> EngineVersionFolders(string engine)
    {
        var children = ChildDirectories.Under(engine);

        return children.Directories.Concat(children.Links)
            .Select(child => child.Name)
            .Where(name => !name.Equals("Common", StringComparison.OrdinalIgnoreCase))
            .SelectMany(name => new[]
            {
                (name, $"Unreal Engine {name}'s own folder, with its settings and crash reports."),
                (Path.Combine(name, "Saved", "Config"), $"Your editor settings for Unreal Engine {name}."),
            });
    }

    private bool ZenServerIsRunning() => Inspector.FindRunning([ZenServer]).Count > 0;

    /// <summary>
    /// Whether <paramref name="path"/> was listed and holds nothing at any depth. False where it
    /// could not be listed, which is not evidence of an empty cache: the plan then says what it
    /// could not see, rather than the location going unmentioned.
    /// </summary>
    private static bool HoldsNothing(string path)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(LongPath.Extended(path)).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }

        return !DirectoryContent.IsPresent(path);
    }
}
