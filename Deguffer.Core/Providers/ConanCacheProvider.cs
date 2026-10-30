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
/// asked, and its answer used.</para>
///
/// <para><b>Held back while Conan runs.</b> Conan's cache is not safe for concurrent use, and
/// <c>-t</c> removes every unfinished build in the store, which is exactly what a build in progress
/// looks like. A command step has no check the run asks again before it starts, so the plan offers
/// no step at all while a <c>conan</c> process is running.</para>
/// </summary>
public sealed class ConanCacheProvider : CleanupProviderBase
{
    private const string Clean = "cache clean -s -b -t";

    private readonly PassMemo<Task<ConanLocation?>> _location = new();

    public ConanCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
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
    public override IReadOnlyList<ToolRoot> ToolRoots => [HomeRoot(DefaultHome)];

    /// <summary>The home and the store Conan reports, which a <c>.conanrc</c>, <c>CONAN_HOME</c> or <c>core.cache:storage_path</c> moves.</summary>
    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Environment.FindExecutable("conan") is { } conan
            && await LocateAsync(conan, ct).ConfigureAwait(false) is ConanLocation.Found found
            ?
            [
                HomeRoot(found.Home),
                new ToolRoot(
                    found.Store,
                    "This is Conan's package store, which holds every recipe and package binary Conan "
                    + "has, and the database that lists them. Clearing its sources and build folders is "
                    + "offered on the Storage page, where Conan's own command does it.",
                    static _ => false),
            ]
            : [];

    /// <summary>
    /// Every folder named <c>s</c> or <c>b</c> in the store, and its <c>t</c>, in the default store, in
    /// the store of the home worked out without Conan, and in the store Conan reports. Wider than the
    /// clean, which spares a complete local build's package, so a duplicate search errs towards
    /// keeping a copy.
    /// </summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        List<string> stores = [Path.Combine(DefaultHome, ConanConfigOutput.DefaultStoreFolder)];

        if (OwnHome() is ConanHomeAnswer.Found own)
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
                },
            ],
            ProtectedPaths = protectedPaths,
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = contents.Unreadable,
        };
    }

    /// <summary>
    /// §5.6. The home's configuration from <c>conan/internal/cache/home_paths.py</c>, the store and its
    /// database, and everything in the store the clean leaves: every recipe and package folder, and
    /// in each of them every folder but the sources and the build.
    /// </summary>
    private static IReadOnlyList<ProtectedPath> BuildProtectedPaths(ConanLocation.Found found, ConanStoreReading contents)
    {
        var home = found.Home;

        return Protect(
        [
            (home, "Conan's home must survive: only sources and build folders in its store are cleared."),
            (Path.Combine(home, "global.conf"), "Conan's configuration."),
            (Path.Combine(home, "global_user.conf"), "Conan's configuration."),
            (Path.Combine(home, "profiles"), "The profiles your builds use."),
            (Path.Combine(home, "remotes.json"), "The remotes Conan downloads from."),
            (Path.Combine(home, "settings.yml"), "Conan's settings model."),
            (Path.Combine(home, "settings_user.yml"), "Your additions to Conan's settings model."),
            (Path.Combine(home, "extensions"), "Your hooks, plugins, deployers and generators."),
            (Path.Combine(home, "audit_providers.json"), "Conan's audit provider configuration."),
            (Path.Combine(home, "config_version.json"), "Which configuration Conan installed."),
            (Path.Combine(home, ".local_recipes_index"), "Local recipe repositories."),
            (Path.Combine(home, "version.txt"), "The Conan version the home was last migrated to."),
            (Path.Combine(home, "sources"), "Conan's backup of downloaded sources, which this clean is never asked to remove."),
            (found.Store, "Conan's package store must survive: only sources and build folders inside it are cleared."),
            (Path.Combine(found.Store, ConanStoreContents.Database), "Conan's database of every recipe and package in the store."),
            .. contents.Kept,
        ]);
    }

    private static ToolRoot HomeRoot(string home) => new(
        home,
        "This is Conan's home. Its configuration, your profiles and remotes, and the package store "
        + "that holds everything Conan built or downloaded are all in here. Clearing its sources and "
        + "build folders is offered on the Storage page, where Conan's own command does it.",
        static _ => false);

    private ConanHomeAnswer OwnHome() => ConanHome.Resolve(Environment, ProcessRunner.WorkingDirectoryFor(Environment));

    /// <summary>
    /// Conan's home and store, asked of Conan once per pass, and null without asking where the home
    /// is not there to ask about.
    /// </summary>
    private Task<ConanLocation?> LocateAsync(string conan, CancellationToken ct) =>
        _location.Get(() => OwnHome() is ConanHomeAnswer.Found own && LongPath.ProbeDirectory(own.Home) is PathPresence.Present
            ? ConanLocation.AskAsync(Runner, conan, ct)
            : Task.FromResult<ConanLocation?>(null));
}
