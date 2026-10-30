using System.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The sources, build folders and temporary folders in Conan 2's package store, cleared with Conan's
/// own <c>conan cache clean</c>. Researched rather than measured: no Conan was installed on the
/// machine this was written against. The source read is Conan 2.34.0, commit
/// <c>70b4cf60b5787ebf1a1af16ac45a11491be0d4c3</c>, cited per file on the types this one uses.
///
/// <para><b>§5.1, with the scope pinned.</b> The step is <c>cache clean -s -b -t</c> with no pattern,
/// so every recipe and package in the cache (<c>cache_clean</c> in
/// <c>conan/cli/commands/cache.py</c>). Each flag is named because with none Conan applies its own
/// defaults, which today include <c>-d</c> and are a choice a later release can change, and the step
/// has to mean the same thing on every machine it runs on. <c>-d</c> is left out on purpose: it
/// removes each <c>d</c> folder, and
/// <c>d/metadata</c> holds a package's metadata, which for a package made with <c>conan create</c>
/// and never uploaded exists nowhere else. <c>-bs</c> is left out because the backup-sources cache
/// can be configured to a folder outside the home that this provider has never looked at. Never
/// <c>conan remove</c>, which removes the packages themselves. The command never prompts, never
/// reads its input, and prints nothing on success; it exits 0 or 1 (<c>conan/cli/exit_codes.py</c>).</para>
///
/// <para><b>Conan is asked nothing until its home is known to be there.</b> Every Conan command,
/// <c>conan config home</c> included, creates the home when it is missing, so the home is first
/// worked out as Conan would work it out (<see cref="ConanHome"/>), and only once it exists is Conan
/// asked. Its answers must agree with Deguffer's own.</para>
///
/// <para><b>A home or store a setting names is vetted before Conan is started.</b>
/// <c>CONAN_HOME</c>, a <c>.conanrc</c> and <c>core.cache:storage_path</c> can name any folder, and
/// examining one as Conan's declares everything in it Conan's (§5.6, §7.1). Conan's first command
/// makes the store and a database in it, so the store is read from the home's configuration
/// (<see cref="ConanGlobalConf"/>) and judged before Conan is asked anything. One
/// <see cref="ConfiguredFolder"/> declines is left alone: no step, and only Conan's own files in it
/// are refused in Explore.</para>
///
/// <para><b>Held back while Conan runs, and held to what the plan saw.</b> Conan's cache is not safe
/// for concurrent use, and <c>-t</c> removes every unfinished build in the store, which is exactly
/// what a build in progress looks like. The plan offers no step while a <c>conan</c> process is
/// running. Immediately before it starts, the step asks again, and also checks that the home, the
/// configuration, the store's <c>t</c> and <c>b</c> and its linked folders are as the plan read them
/// (<see cref="ConanCleanTarget"/>), because any of them can change while the preview is on
/// screen.</para>
/// </summary>
public sealed class ConanCacheProvider : CleanupProviderBase
{
    private const string Clean = "cache clean -s -b -t";

    private readonly ISystemDirectories _system;
    private readonly ConanRcWalk _conanRcWalk;
    private readonly PassMemo<ConanHomeAnswer> _ownHome = new();
    private readonly PassMemo<Task<ConanLocation>> _location = new();

    public ConanCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null)
        : this(ConanRcWalk.ToDriveRoot, environment, runner, inspector, scanner, system, volumes)
    {
    }

    /// <param name="conanRcWalk">
    /// How far above the folder Conan runs in a <c>.conanrc</c> is looked for: the drive's root, as
    /// Conan looks, everywhere but a test, which ends it inside the folder it made.
    /// </param>
    internal ConanCacheProvider(
        ConanRcWalk conanRcWalk,
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
        _conanRcWalk = conanRcWalk;
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "conan";

    public override string Name => "Conan package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "A package built from source again fetches its sources and builds from a fresh folder. Every "
        + "recipe and package binary in the cache, and everything Conan downloaded, is untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Conan, the package manager for C and C++",
        Publisher = "JFrog",
        Purpose = "Conan keeps the source code it fetched and the folders it built packages in "
            + "beside the packages themselves, so that a package can be built again without "
            + "fetching or configuring anything a second time.",
        Recommendation = "Deguffer runs Conan's own cache clean, limited to sources, build folders "
            + "and unfinished builds. The packages you built or downloaded stay, and so do their "
            + "metadata and your configuration.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["conan"];

    /// <summary>Conan's home when nothing names another.</summary>
    public string DefaultHome => Path.Combine(Environment.UserProfile, ConanHome.DefaultFolder);

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside. Nothing in the home is offered there: its
    /// configuration sits beside the package store, and which folders in the store are disposable is
    /// Conan's database to say, not a name.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => [ConanDeclarations.Home(DefaultHome)];

    /// <summary>
    /// The home and the store Conan reports, which a <c>.conanrc</c>, <c>CONAN_HOME</c> or
    /// <c>core.cache:storage_path</c> moves, and a home or store set to a folder this declines, where
    /// only Conan's own files are refused. See <see cref="ToolRoot.Sparing"/>.
    /// </summary>
    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        if (Environment.FindExecutable("conan") is not { } conan)
        {
            return [];
        }

        return await LocateAsync(conan, ct).ConfigureAwait(false) switch
        {
            ConanLocation.Found found => [ConanDeclarations.Home(found.Home), ConanDeclarations.Store(found.Store)],
            ConanLocation.HomeDeclined home => [ConanDeclarations.DeclinedHome(home.Home)],
            ConanLocation.StoreDeclined store => [ConanDeclarations.Home(store.Home), ConanDeclarations.DeclinedStore(store.Store)],
            _ => [],
        };
    }

    /// <summary>
    /// Every folder named <c>s</c> or <c>b</c> in the store, and its <c>t</c>, in the default store, in
    /// the store of the home worked out without Conan, and in the store Conan reports. Wider than the
    /// clean, which spares a complete local build's package, so a duplicate search errs towards
    /// keeping a copy. Never a store this declines, where the clean is never run.
    /// </summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        List<string> stores = [Path.Combine(DefaultHome, ConanConfigOutput.DefaultStoreFolder)];

        if (OwnHome() is ConanHomeAnswer.Found own && WhyNotOwned(own.Home) is null)
        {
            stores.Add(Path.Combine(own.Home, ConanConfigOutput.DefaultStoreFolder));
        }

        if (Environment.FindExecutable("conan") is { } conan
            && await LocateAsync(conan, ct).ConfigureAwait(false) is ConanLocation.Found found)
        {
            stores.Add(found.Store);
        }

        return
        [
            .. stores.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(store => new[]
            {
                CleanedPlace.FoldersNamed(store, [ConanStoreContents.SourceFolder, ConanStoreContents.BuildFolder]),
                CleanedPlace.Whole(Path.Combine(store, ConanStoreContents.TempFolder)),
            }),
        ];
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("conan") is not null);

    /// <summary>A <c>.conanrc</c>, <c>CONAN_HOME</c> and <c>global.conf</c> can all move the home or the store between passes.</summary>
    public override void InvalidateCaches()
    {
        _ownHome.Forget();
        _location.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (Environment.FindExecutable("conan") is not { } conan)
        {
            return EmptyPlan("Conan is not installed on this machine.");
        }

        switch (OwnHome())
        {
            case ConanHomeAnswer.Refused refused:
                return UnreadableRootPlan(refused.Path);

            case ConanHomeAnswer.Unknown unknown:
                return UnexaminedPlan($"{unknown.Why} Nothing is offered, and Conan was not started.");

            case ConanHomeAnswer.Found own when NothingToPlanFor(
                own.Home,
                $"Conan is installed, but its home, {own.Home} from {own.Source}, does not exist yet.") is { } nothing:
                return nothing;
        }

        if (Inspector.FindRunning(ConflictingProcessNames) is { Count: > 0 } running)
        {
            var held = new PlanNote(
                PlanNoteSeverity.Warning,
                $"Left Conan's cache alone because {string.Join(", ", running)} is running. Conan's clean "
                + "removes unfinished builds, and a build in progress is one. Close Conan and preview again.");

            return UnexaminedPlan(held.Message) with { Notes = [held] };
        }

        return await LocateAsync(conan, ct).ConfigureAwait(false) switch
        {
            ConanLocation.Found found => await PlanCleanAsync(conan, found, ct).ConfigureAwait(false),
            ConanLocation.Unknown unknown => UnexaminedPlan(unknown.Why),
            ConanLocation.HomeDeclined home => EmptyPlan(
                $"Conan is set to keep its home in {home.Home}, by {home.Source}, and Deguffer will not run "
                + $"Conan's clean there: {home.Why} It is leaving it alone."),
            ConanLocation.StoreDeclined store => EmptyPlan(
                $"Conan is set to keep its package store in {store.Store}, and Deguffer will not run Conan's "
                + $"clean there: {store.Why} It is leaving it alone."),

            // Remembered from earlier in this pass, when the home was not yet there to ask about.
            _ => UnexaminedPlan("Conan's home was not there when this scan first looked, so Conan was not asked about it. Scan again."),
        };
    }

    private async Task<CleanupPlan> PlanCleanAsync(string conan, ConanLocation.Found found, CancellationToken ct)
    {
        if (NothingToPlanFor(found.Store, $"Conan's package store does not exist yet ({found.Store}).") is { } nothing)
        {
            return nothing;
        }

        var contents = ConanStoreContents.Read(found.Store, ct);

        if (contents.Unrecognised.Count > 0)
        {
            var withheld = UnexaminedPlan(Unrecognised(found.Store, contents.Unrecognised));

            return withheld with
            {
                ProtectedPaths = BuildProtectedPaths(found, contents),
                Notes = [.. withheld.Notes, .. contents.Notes],
                HasUnreadableRoot = contents.Unreadable,
            };
        }

        if (contents.Measured.Count == 0)
        {
            var clear = EmptyPlan(
                $"Conan's package store at {found.Store} holds no sources, build folders or unfinished builds.");

            return clear with { Notes = [.. clear.Notes, .. contents.Notes], HasUnreadableRoot = contents.Unreadable };
        }

        var protectedPaths = BuildProtectedPaths(found, contents);
        var measured = await MeasureAllAsync(contents.Measured, ct).ConfigureAwait(false);

        List<PlanNote> notes =
        [
            new(PlanNoteSeverity.Information,
                $"Conan reports its home as {found.Home} and its package store as {found.Store}. Its clean "
                + "removes the sources, the build folders and the unfinished builds there, and leaves every "
                + "recipe, every package binary, the downloads and their metadata."),
            .. contents.Notes,
        ];

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps =
            [
                new RunCommandStep(conan, Clean, "Clear Conan's sources and build folders using its own command")
                {
                    // Conan builds from a source folder, and reuses a build folder, that is there and not
                    // marked dirty, and its clean marks nothing before it removes. Ended part-way it could
                    // leave half a folder that the next build takes for a whole one. Left to finish.
                    WhenCancelled = CancelledCommand.Finishes,
                    Estimated = measured.Total,
                    MeasuredPaths = contents.Measured,
                    TargetCheck = new ConanCleanTarget(Inspector, ConflictingProcessNames, ResolveHome, found, contents.HeldTo),
                },
            ],
            ProtectedPaths = protectedPaths,
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = contents.Unreadable,
        };
    }

    /// <summary>
    /// §5.6. The home's configuration, the store and its database, and everything in the store the
    /// clean leaves: every recipe and package folder, in each of them every folder but the sources
    /// and the build, and anything the clean would take that Conan did not make.
    /// </summary>
    private static IReadOnlyList<ProtectedPath> BuildProtectedPaths(ConanLocation.Found found, ConanStoreReading contents) =>
        Protect([.. ConanDeclarations.Survivors(found.Home, found.Store), .. contents.Kept]);

    private static string Unrecognised(string store, IReadOnlyList<string> entries)
    {
        var named = string.Join(", ", entries.Take(3).Select(entry => $"'{LongPath.Relative(store, entry)}'"));
        var rest = entries.Count > 3 ? $" and {entries.Count - 3} more" : string.Empty;

        return $"Conan's package store at {store} holds something Conan did not make, or that would not be "
            + $"listed, where its clean removes by shape rather than by name: {named}{rest}. Deguffer will not "
            + "run the clean while it is there. Move it out of the store and scan again.";
    }

    /// <summary>
    /// The home worked out without Conan, once per pass: a walk to the drive's root for a
    /// <c>.conanrc</c> and a read of the one it finds, which the plan, the cleaned places and the
    /// question to Conan all need to agree on.
    /// </summary>
    private ConanHomeAnswer OwnHome() => _ownHome.Get(ResolveHome);

    /// <summary>The home worked out afresh, for the pass and again when the clean runs.</summary>
    private ConanHomeAnswer ResolveHome() =>
        ConanHome.Resolve(Environment, ProcessRunner.WorkingDirectoryFor(Environment), _conanRcWalk);

    /// <summary>Why a home or store a setting names must not be examined as Conan's, or null where it may.</summary>
    private string? WhyNotOwned(string folder) => ConfiguredFolder.WhyNotOwned(
        folder, Environment, _system, Volumes, TempRoots.Resolve(Environment, _system, Volumes).AccountFolders);

    /// <summary>
    /// Conan's home and store, asked of Conan once per pass. A home this declines is never asked
    /// about, and nor is one that is not there, because asking would make it.
    /// </summary>
    private Task<ConanLocation> LocateAsync(string conan, CancellationToken ct) =>
        _location.Get(() => OwnHome() switch
        {
            ConanHomeAnswer.Found own when WhyNotOwned(own.Home) is { } declined =>
                Task.FromResult<ConanLocation>(new ConanLocation.HomeDeclined(own.Home, own.Source, declined)),
            ConanHomeAnswer.Found own when LongPath.ProbeDirectory(own.Home) is PathPresence.Present =>
                AskAboutAsync(conan, own, ct),
            _ => Task.FromResult<ConanLocation>(new ConanLocation.NotAsked()),
        });

    /// <summary>
    /// The store the home's configuration names, judged before Conan is started, because the first
    /// question Conan is asked makes that folder and a database in it. See <see cref="ConanGlobalConf"/>.
    /// </summary>
    private Task<ConanLocation> AskAboutAsync(string conan, ConanHomeAnswer.Found own, CancellationToken ct) =>
        ConanGlobalConf.Read(own.Home) switch
        {
            ConanConfReading.Plain plain when WhyNotOwned(plain.Store) is { } declined =>
                Task.FromResult<ConanLocation>(new ConanLocation.StoreDeclined(own.Home, plain.Store, declined)),
            ConanConfReading.Plain plain => ConanLocation.AskAsync(Runner, conan, own, plain, WhyNotOwned, ct),
            ConanConfReading.Unusable unusable => Task.FromResult<ConanLocation>(
                new ConanLocation.Unknown($"{unusable.Why} Nothing is offered, and Conan was not started.")),
            _ => throw new UnreachableException("A reading of Conan's configuration is plain or unusable."),
        };
}
