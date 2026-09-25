using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The shaders Steam downloads for each game so that it does not stutter the first time it runs,
/// kept per game in <c>steamapps\shadercache\&lt;appid&gt;</c> in every Steam library. Nothing was
/// measured on the machine this was written on, where shader pre-caching was switched off and the
/// folder was empty. Reports from machines with large libraries and pre-caching on run past 60 GB.
///
/// <para><b>Tier 2, and the reason is direct rather than cautious.</b> A shader cache the game
/// rebuilds on its own is Tier 1, as the graphics drivers' caches are. This one is downloaded:
/// Valve distributes the compiled pipelines, and switching pre-caching off and on again is recorded
/// as fetching all of them again from Valve. Removing it costs gigabytes of download for the games
/// still played, which is the second tier by definition. A separate provider from
/// <see cref="SteamCacheProvider"/> because a provider has one tier, and that one's caches are
/// Tier 1.</para>
///
/// <para><b>Every library, and only Steam's list says where they are.</b> A library can be on any
/// drive, so the folder beside the program is only the first of them.
/// <see cref="SteamLibraryFolders"/> reads the rest from Steam's own list, and a list that cannot be
/// read is a sentence on the plan rather than a smaller number nobody can account for.</para>
///
/// <para><b>§5.2 twice over.</b> <c>steamapps</c> is never a target: it holds every installed
/// game, and <c>downloading</c>, <c>temp</c>, <c>workshop</c>, <c>sourcemods</c> and every
/// <c>appmanifest_*.acf</c> sit beside <c>shadercache</c>. <c>shadercache</c> is a container too,
/// so only its children are named, and only a child whose name is a Steam application id
/// (<see cref="SteamAppId"/>). Anything else in it is Tier 4, and is named on the plan and protected
/// rather than skipped in silence.</para>
///
/// <para><b>§5.1 exists and cannot be driven.</b> Steam offers to delete pre-cached shaders under
/// Settings, Shader Pre-Caching, which is a button in a running client and not a command Deguffer
/// can run — the limitation <see cref="SteamCacheProvider"/> records for the web cache. Path
/// deletion is the only route available.</para>
///
/// <para><b>A library is found through Steam's record and not checked for a link</b>, as the install
/// directory is not: whatever a recorded library resolves to is the library Steam uses. What is
/// derived below it — <c>steamapps</c> and <c>shadercache</c> — is checked segment by segment, because
/// a link there points at a tree Steam's record never named.</para>
/// </summary>
public sealed class SteamShaderCacheProvider : CleanupProviderBase
{
    /// <summary>The per-game container, relative to a library.</summary>
    private static readonly string ShaderCacheDirectory = Path.Combine("steamapps", "shadercache");

    /// <summary>
    /// What §5.6 asserts survived in each library whose shader cache was reached, beside the library
    /// and the container. Named rather than covered by an assertion on <c>steamapps</c>, because that
    /// assertion would pass with every game inside it gone.
    /// </summary>
    private static readonly IReadOnlyList<(string RelativePath, string Reason)> Neighbours =
    [
        ("steamapps", "Your installed games, and Steam's records of them."),
        (Path.Combine("steamapps", "common"), "The games themselves, on disk."),
        (Path.Combine("steamapps", "downloading"),
            "The half-downloaded part of an update. Removing it restarts the download."),
        (Path.Combine("steamapps", "temp"), "Steam's working space for an update in progress."),
        (Path.Combine("steamapps", "workshop"), "Workshop content you subscribed to."),
        (Path.Combine("steamapps", "sourcemods"), "Mods for Source games that you installed yourself."),
        (Path.Combine("steamapps", SteamLibraryFolders.ListFileName), "Steam's list of your game libraries."),
        ("libraryfolder.vdf", "Steam's record that this folder is one of its libraries."),
        (ShaderCacheDirectory,
            "The folder Steam keeps every game's shader cache in. Only the folders for single games "
            + "inside it are removed."),
    ];

    private readonly SteamDiscovery _discovery;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    public SteamShaderCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
        => _discovery = new SteamDiscovery(Environment);

    public override string Id => "steam-shader-cache";

    public override string Name => "Steam shader pre-cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "While shader pre-caching is on, Steam downloads the shaders again from Valve for each game "
        + "you play, which can be several gigabytes. With it off, a game compiles its own shaders as "
        + "it runs, and stutters the first time each scene appears. Your games, your saves, your "
        + "Workshop content and any download in progress are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Steam client",
        Publisher = "Valve",
        Purpose = "Compiling a game's shaders on your own machine makes it stutter the first time it "
            + "runs, so Steam downloads them already compiled for your graphics card and keeps them "
            + "per game, in every library you have.",
        Recommendation = "Clear it for games you no longer play, or when you need the space. Steam "
            + "downloads it again for any game you still play, and that download can run to "
            + "gigabytes. Deguffer removes the folders for single games inside the shader cache and "
            + "nothing else in your libraries.",
    };

    /// <summary>
    /// §5.3. The client downloads and processes shader caches while it runs, so removing one during
    /// that download is the obvious hazard.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["steam", "steamwebhelper"];

    /// <summary>
    /// §5.2 as §7.1 reads it, per library: <c>steamapps</c> recognises nothing, and the container
    /// inside it recognises application ids. Two levels, because a <see cref="ToolRoot"/> classifies
    /// a directory's immediate children and the caches are two levels down.
    ///
    /// <para>Declared on <c>steamapps</c> rather than on the library folder, because the library
    /// folder is whatever the user picked. Were it a drive's root, a root that recognises nothing
    /// would refuse everything on that drive in Explore. <c>steamapps</c> is Steam's own folder
    /// wherever the library is.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => _toolRoots ??= DeclareToolRoots();

    public override void InvalidateCaches()
    {
        _discovery.Invalidate();
        _toolRoots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is a shader cache for at least one game, with something in it, in some library — or
    /// something standing in the way of finding out.
    ///
    /// <para>An empty container is not presence. Steam keeps <c>shadercache</c> with pre-caching
    /// switched off, as the machine this was written on showed, and a row for it would report a source
    /// with nothing in it. What stands in the way is presence, because a row that never appears is the one
    /// state nothing downstream can correct: an install Windows would not describe, a list of libraries
    /// that could not be read, a library or folder Windows would not describe, or a link.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        if (_discovery.Libraries is not { } libraries)
        {
            return Task.FromResult(InstallUnreached() is not null);
        }

        return Task.FromResult(!libraries.IsComplete || libraries.Folders.Any(MayHoldACache));
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (_discovery.Libraries is not { } libraries)
        {
            return PlanWithoutAnInstall();
        }

        var examination = new Examination(new SteamAppManifests(libraries));

        if (ListingNote(libraries) is { } listingNote)
        {
            examination.Notes.Add(listingNote);
        }

        foreach (var library in libraries.Folders)
        {
            ct.ThrowIfCancellationRequested();
            Collect(library, examination, ct);
        }

        if (examination.Targets.Count == 0
            && examination.Declined.Count == 0
            && !examination.Unreadable
            && libraries.IsComplete)
        {
            return EmptyPlan("No Steam library on this machine holds a shader cache.");
        }

        var (steps, measured) = await PlanDeletionsAsync(examination.Targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            examination.Notes.Add(scanNote);
        }

        if (BuildRunningProcessNote() is { } warning)
        {
            examination.Notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect([.. examination.Survivors, .. examination.Declined]),
            Notes = examination.Notes,
            Fallback = measured.Fallback,

            // A list that was read and not understood is Deguffer declining to guess, so it is the
            // not-examined flag. A list or folder Windows would not describe is the other one.
            WasNotExamined = examination.Targets.Count == 0
                && (examination.Declined.Count > 0 || libraries.Listing is SteamLibraryListing.Malformed),
            HasUnreadableRoot = examination.Unreadable || libraries.Listing is SteamLibraryListing.Unreadable,
        };
    }

    /// <summary>
    /// The plan for a machine whose Steam install was not found, which is where the list of libraries
    /// is kept. The same three answers <see cref="SteamCacheProvider"/> gives for the cache it keeps
    /// beside the program.
    /// </summary>
    private CleanupPlan PlanWithoutAnInstall()
    {
        if (InstallUnreached() is not { } unreached)
        {
            return EmptyPlan("Steam is not installed for this user.");
        }

        return _discovery.Install.UnreachedRoot is { } refused
            ? UnreadableRootPlan(refused) with { Notes = [unreached] }
            : UnexaminedPlan(unreached.Message);
    }

    private PlanNote? InstallUnreached() =>
        _discovery.UnreachedInstallNote("the shader cache in Steam's game libraries");

    /// <summary>
    /// The sentence owed where the list of libraries could not be used, so that a plan covering only
    /// the library beside the program does not read as covering all of them.
    /// </summary>
    private static PlanNote? ListingNote(SteamLibraries libraries) => libraries.Listing switch
    {
        SteamLibraryListing.Unreadable => new PlanNote(
            PlanNoteSeverity.Warning,
            $"Windows would not let Deguffer read Steam's list of game libraries at "
            + $"'{libraries.ListPath}', so only the library beside the Steam program was examined. A "
            + "shader cache in any other library was neither cleared nor ruled out."),
        SteamLibraryListing.Malformed => new PlanNote(
            PlanNoteSeverity.Warning,
            $"Deguffer could not make sense of Steam's list of game libraries at "
            + $"'{libraries.ListPath}', so only the library beside the Steam program was examined. A "
            + "shader cache in any other library was neither cleared nor ruled out."),
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="library"/> may hold a game's shader cache with something in it. See
    /// <see cref="IsPresentAsync"/> for why an obstacle answers yes.
    /// </summary>
    private static bool MayHoldACache(string library)
    {
        switch (LongPath.ProbeDirectory(library))
        {
            case PathPresence.Absent:
                return false;

            case PathPresence.Refused:
                return true;
        }

        var container = Path.Combine(library, ShaderCacheDirectory);

        if (DerivedPath.FirstObstacleBetween(library, container) is not null)
        {
            return true;
        }

        if (!LongPath.DirectoryExists(container))
        {
            return false;
        }

        var scan = ChildDirectories.Under(container);

        return scan.Unreadable
            || scan.Directories.Any(child => SteamAppId.IsAppId(child.Name) && DirectoryContent.IsPresent(child.FullName));
    }

    /// <summary>
    /// §5.2 for one library: reach its container through folders that are not links, classify every
    /// child, target the games, and name everything else.
    /// </summary>
    private static void Collect(string library, Examination examination, CancellationToken ct)
    {
        switch (LongPath.ProbeDirectory(library))
        {
            // A library on a drive that is not connected, or one removed since Steam listed it. There
            // is nothing on this machine to reclaim from it.
            case PathPresence.Absent:
                return;

            case PathPresence.Refused:
                examination.Notes.Add(UnreadableRoot.UnreachedNote(library));
                examination.Unreadable = true;
                return;
        }

        var container = Path.Combine(library, ShaderCacheDirectory);

        if (DerivedPath.FirstObstacleBetween(library, container) is { } obstacle)
        {
            if (obstacle.IsLink)
            {
                examination.Notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{obstacle.Path}' alone: it is a link to somewhere else, and Deguffer does "
                    + "not look through a link."));
                examination.Declined.Add((
                    obstacle.Path,
                    "A link rather than a directory, so what it points at was never classified."));
            }
            else
            {
                examination.Notes.Add(UnreadableRoot.UnreachedNote(obstacle.Path));
                examination.Unreadable = true;
            }

            return;
        }

        if (!LongPath.DirectoryExists(container))
        {
            return;
        }

        var scan = ChildDirectories.Under(container);

        if (scan.Unreadable)
        {
            examination.Notes.Add(UnreadableRoot.Note(container));
            examination.Unreadable = true;
            return;
        }

        examination.Survivors.Add((
            library,
            "A Steam library, holding your games. Only the shader caches inside it are removed."));
        examination.Survivors.AddRange(Neighbours.Select(n => (Path.Combine(library, n.RelativePath), n.Reason)));

        foreach (var link in scan.Links)
        {
            var path = LongPath.Display(link.FullName);

            examination.Notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not delete "
                + "through a link."));
            examination.Declined.Add((path, "A link rather than a directory, so what it points at was never classified."));
        }

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(child.FullName);

            if (!SteamAppId.IsAppId(child.Name))
            {
                const string reason =
                    "Not a folder Steam made for one game, so nobody established what it is and it is "
                    + "left alone.";

                examination.Notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{path}' alone: {reason}"));
                examination.Declined.Add((path, reason));
                continue;
            }

            var app = examination.Manifests.Describe(child.Name);

            examination.Targets.Add(new DeletionTarget(
                path,
                $"Shaders Steam downloaded for {app.DisplayName}. Steam downloads them again from Valve "
                + "while shader pre-caching is on.",

                // The id is the game wherever its library is, so a game kept stays kept if the user
                // moves it to another drive.
                Identity: new ItemIdentity(app.Id, app.Name ?? $"Steam app {app.Id}"),
                Facets: Facets(app),
                Group: library));

            // The game's manifest is the record that it is installed, and it sits two levels above
            // the folder being removed: the sibling an over-broad rule would take next.
            examination.Survivors.Add((
                Path.Combine(library, SteamAppManifests.RelativePath(app.Id)),
                $"Steam's record that {app.DisplayName} is installed."));
        }
    }

    private static IReadOnlyList<ItemFacet> Facets(SteamApp app) => app.IsInstalled switch
    {
        true => [new ItemFacet("App ID", app.Id), new ItemFacet("Installed", "Yes")],
        false => [new ItemFacet("App ID", app.Id), new ItemFacet("Installed", "No")],
        null => [new ItemFacet("App ID", app.Id)],
    };

    private IReadOnlyList<ToolRoot> DeclareToolRoots()
    {
        if (_discovery.Libraries is not { } libraries)
        {
            return [];
        }

        var roots = new List<ToolRoot>(libraries.Folders.Count * 2);

        foreach (var library in libraries.Folders)
        {
            roots.Add(new ToolRoot(
                Path.Combine(library, "steamapps"),
                "This is where Steam keeps the games in one of your libraries. Your games, their "
                + "Workshop content and any download in progress are in here, and Deguffer removes "
                + "none of them.",
                static _ => false));

            roots.Add(new ToolRoot(
                Path.Combine(library, ShaderCacheDirectory),
                "This is the folder Steam keeps every game's shader cache in. Deguffer removes the "
                + "folders for single games inside it and nothing else.",
                SteamAppId.IsAppId));
        }

        return roots;
    }

    /// <summary>What one planning pass has found so far, across every library.</summary>
    private sealed class Examination(SteamAppManifests manifests)
    {
        public SteamAppManifests Manifests { get; } = manifests;

        public List<DeletionTarget> Targets { get; } = [];

        public List<(string Path, string Reason)> Declined { get; } = [];

        public List<(string Path, string Reason)> Survivors { get; } = [];

        public List<PlanNote> Notes { get; } = [];

        public bool Unreadable { get; set; }
    }
}
