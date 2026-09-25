using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The store and library artwork the Steam client downloads for every game it shows, kept per game in
/// <c>appcache\librarycache\&lt;appid&gt;</c> under the install directory. 1,675 MB across 3,911
/// games on the machine it was measured on, where the oldest files were years old: whatever Steam
/// evicts, it had not evicted those.
///
/// <para><b>Steam fetches it again, which was observed rather than assumed.</b> Folders for three
/// games were moved out of a real client's cache with Steam closed, leaving Steam's own index,
/// <c>assetcache.vdf</c>, listing them as present. The client was then started and the games shown
/// in its library. Every piece of their artwork was drawn, and Steam wrote the files it needed back
/// into new folders within seconds. The index is therefore not something removing a game's folder
/// has to keep in step, and it is never a target: it is Steam's record, and it describes the games
/// that stay as much as the ones that go.</para>
///
/// <para><b>Tier 2 rather than 1, for the one thing that makes it more than a cache.</b> Replacing
/// these files by hand is a long-standing way of changing a game's library artwork. Steam's
/// supported route writes to <c>userdata</c>, which is never reached here, but a hand-edited file in
/// this folder cannot be told from a downloaded one, and Steam's copy replaces it for good. §3 keeps
/// Tier 1 for what loses nothing and ticks it without asking, so this row is not ticked and asks to
/// be acknowledged, and each game is an item that can be kept. For the same reason it is its own row
/// rather than a part of <see cref="SteamCacheProvider"/>.</para>
///
/// <para><b>§5.2.</b> The folder is a container: only its children are named, and only a child
/// whose name is a Steam application id (<see cref="SteamAppId"/>), or a file an older client wrote
/// straight into the folder as <c>&lt;appid&gt;_&lt;art&gt;.jpg</c> or <c>.png</c>. Anything else
/// is Tier 4, named on the plan and protected.</para>
///
/// <para><b>§5.1 does not apply.</b> Steam's Settings, Downloads, Clear Download Cache is reported
/// to clear <c>appcache</c>, which Valve has not confirmed for this folder, and either way it is a
/// button in a running client rather than a command Deguffer can run.</para>
/// </summary>
public sealed class SteamLibraryArtworkProvider : CleanupProviderBase
{
    /// <summary>Steam's index of the artwork it has saved, a file in the container.</summary>
    private const string AssetIndexName = "assetcache.vdf";

    private const string LinkReason =
        "A link rather than a folder, so what it points at was never classified.";

    private const string UnrecognisedReason =
        "Not something Steam made for one game, so nobody established what it is and it is left alone.";

    /// <summary>The per-game container, relative to the install directory.</summary>
    private static readonly string LibraryCacheDirectory = Path.Combine("appcache", "librarycache");

    /// <summary>
    /// What §5.6 asserts survived beside the container. <c>appcache\httpcache</c> is left out on
    /// purpose: it is <see cref="SteamCacheProvider"/>'s target, and a run may take both rows.
    /// </summary>
    private static readonly IReadOnlyList<(string RelativePath, string Reason)> Neighbours =
    [
        ("steamapps", "Your installed games."),
        (Path.Combine("steamapps", "common"), "The games themselves, on disk."),
        (Path.Combine("steamapps", "downloading"),
            "The half-downloaded part of an update. Removing it restarts the download."),
        ("userdata", "Your Steam settings, cloud saves, screenshots and any library artwork you chose "
            + "through Steam, per account."),
        ("config", "Steam's own configuration, including who is signed in on this computer."),
        (Path.Combine("appcache", "appinfo.vdf"), "Steam's index of the applications it knows about."),
        (Path.Combine("appcache", "packageinfo.vdf"), "Steam's index of the packages it knows about."),
        (LibraryCacheDirectory,
            "The folder Steam keeps every game's library artwork in. Only what belongs to single games "
            + "inside it is removed."),
        (Path.Combine(LibraryCacheDirectory, AssetIndexName),
            "Steam's index of the artwork it has saved. It describes the games that stay as well."),
    ];

    private readonly SteamDiscovery _discovery;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    public SteamLibraryArtworkProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        SteamDiscovery? discovery = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
        => _discovery = discovery ?? new SteamDiscovery(Environment);

    public override string Id => "steam-library-artwork";

    public override string Name => "Steam library artwork";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "Steam downloads each game's artwork again the next time it shows the game in your library. "
        + "Until it can, for example while you are offline, a game may show a blank picture. If you "
        + "replaced any of these pictures by hand rather than through Steam's own Manage, Set custom "
        + "artwork, your picture is lost and Steam's comes back: keep that game. Your games, your "
        + "cloud saves and artwork you set through Steam are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Steam client",
        Publisher = "Valve",
        Purpose = "Steam downloads the capsule, banner and logo it shows for every game in your "
            + "library, including games you do not have installed, and keeps them per game. It keeps "
            + "them for years.",
        Recommendation = "Clear it when you need the space: Steam downloads a game's artwork again "
            + "when it next shows the game. Keep any game whose pictures you replaced by hand. "
            + "Deguffer removes the artwork for single games inside Steam's artwork folder and "
            + "nothing else in Steam's folder.",
    };

    /// <summary>§5.3. The client writes this folder while it shows the library.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => SteamDiscovery.ProcessNames;

    /// <summary>
    /// §5.2 as §7.1 reads it: the container recognises what this provider targets inside it, and
    /// nothing else. <c>appcache</c> and the install directory above it are declared by
    /// <see cref="SteamCacheProvider"/>, which already refuses them.
    ///
    /// <para>The child is looked at rather than judged by its name, because the plan's answer
    /// depends on what it is: <c>440</c> is a game's artwork as a folder and unrecognised as a file,
    /// <c>440_header.jpg</c> the reverse, and a game's folder that is a link is never offered.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => _toolRoots ??=
        _discovery.Install.Root is { } install
            ? [new ToolRoot(
                Path.Combine(install, LibraryCacheDirectory),
                "This is the folder Steam keeps every game's library artwork in, with its index of that "
                + "artwork. Deguffer removes what belongs to single games inside it and nothing else.",
                name => IsOffered(Path.Combine(install, LibraryCacheDirectory, name)))]
            : [];

    public override void InvalidateCaches()
    {
        _discovery.Invalidate();
        _toolRoots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is one game's artwork on disk, or something standing in the way of finding out: an
    /// install Windows would not describe, a link, or a folder that would not be listed. A row that
    /// never appears is the one state nothing downstream can correct.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        if (_discovery.Install.Root is not { } install)
        {
            return Task.FromResult(InstallUnreached() is not null);
        }

        var container = Path.Combine(install, LibraryCacheDirectory);

        if (DerivedPath.FirstObstacleBetween(install, container) is not null)
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(FolderEntries.Of(container) switch
        {
            null => true,
            var entries => entries.Any(MayHoldArtwork),
        });
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        // The list of libraries is read from the install, so each is there exactly when the other is.
        if (_discovery.Install.Root is not { } install || _discovery.Libraries is not { } libraries)
        {
            return PlanWithoutAnInstall();
        }

        var container = Path.Combine(install, LibraryCacheDirectory);

        if (DerivedPath.FirstObstacleBetween(install, container) is { } obstacle)
        {
            if (!obstacle.IsLink)
            {
                return UnreadableRootPlan(obstacle.Path);
            }

            return UnexaminedPlan(
                $"Leaving '{obstacle.Path}' alone: it is a link to somewhere else, and Deguffer does not "
                + "look through a link.") with
            {
                ProtectedPaths = Protect((obstacle.Path, LinkReason)),
            };
        }

        if (FolderEntries.Of(container) is not { } entries)
        {
            return UnreadableRootPlan(container);
        }

        var examination = Examine(entries, new SteamAppManifests(libraries), ct);

        if (examination.Targets.Count == 0 && examination.Declined.Count == 0)
        {
            return EmptyPlan("Steam is keeping no library artwork on this machine.");
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
            ProtectedPaths = Protect(
            [
                (install, "This is where Steam itself is installed, with your games beside it."),
                .. Neighbours.Select(n => (Path.Combine(install, n.RelativePath), n.Reason)),
                .. examination.Declined,
            ]),
            Notes = examination.Notes,
            Fallback = measured.Fallback,
            WasNotExamined = examination.Targets.Count == 0,
        };
    }

    /// <summary>
    /// Whether a child of the container is presence. An empty game folder is Steam's own leftover
    /// and no reason for a row, and a game folder that would not be listed may hold anything. A link
    /// is presence for the sentence the plan owes about it.
    /// </summary>
    private static bool MayHoldArtwork(FileSystemInfo entry) => Classify(entry) switch
    {
        Kind.GameFolder => DirectoryContent.IsPresent(entry.FullName)
            || ChildDirectories.Under(entry.FullName).Unreadable,
        Kind.FlatArtwork or Kind.Link => true,
        _ => false,
    };

    /// <summary>What a child of the container is, as far as this provider is concerned.</summary>
    private enum Kind
    {
        Unrecognised,
        Link,
        Index,
        GameFolder,
        FlatArtwork,
    }

    /// <summary>
    /// Whether the child at <paramref name="path"/> is one the plan would target. Something Windows
    /// will not describe is neither a folder nor a file here, so it is refused.
    /// </summary>
    private static bool IsOffered(string path)
    {
        var extended = LongPath.Extended(path);
        FileSystemInfo entry = Directory.Exists(extended) ? new DirectoryInfo(extended) : new FileInfo(extended);

        return entry.Exists && Classify(entry) is Kind.GameFolder or Kind.FlatArtwork;
    }

    private static Kind Classify(FileSystemInfo entry) => entry switch
    {
        _ when entry.Attributes.HasFlag(FileAttributes.ReparsePoint) => Kind.Link,
        DirectoryInfo when SteamAppId.IsAppId(entry.Name) => Kind.GameFolder,
        FileInfo when string.Equals(entry.Name, AssetIndexName, StringComparison.OrdinalIgnoreCase) => Kind.Index,
        FileInfo when FlatArtworkAppId(entry.Name) is not null => Kind.FlatArtwork,
        _ => Kind.Unrecognised,
    };

    /// <summary>
    /// The application id in the name of a file an older client wrote straight into the container,
    /// such as <c>440_library_600x900.jpg</c>, or null for any other name. Only a picture qualifies:
    /// those clients wrote nothing else there, so another extension is something nobody established.
    /// </summary>
    private static string? FlatArtworkAppId(string name)
    {
        var separator = name.IndexOf('_', StringComparison.Ordinal);
        var extension = Path.GetExtension(name);

        if (separator <= 0
            || !(extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var id = name[..separator];
        return SteamAppId.IsAppId(id) ? id : null;
    }

    /// <summary>Classify every child of the container, target the games, and name everything else.</summary>
    private static Examination Examine(
        IReadOnlyList<FileSystemInfo> entries,
        SteamAppManifests manifests,
        CancellationToken ct)
    {
        var examination = new Examination();

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            switch (Classify(entry))
            {
                case Kind.GameFolder:
                {
                    var app = manifests.Describe(entry.Name);

                    examination.Targets.Add(Target(
                        app,
                        path,
                        TargetKind.Directory,
                        $"Library artwork Steam downloaded for {app.DisplayName}. Steam downloads it again "
                        + "the next time it shows the game."));
                    break;
                }

                case Kind.FlatArtwork:
                {
                    var app = manifests.Describe(FlatArtworkAppId(entry.Name)!);

                    examination.Targets.Add(Target(
                        app,
                        path,
                        TargetKind.File,
                        $"A picture an older Steam client downloaded for {app.DisplayName}, from before it "
                        + "kept each game's artwork in a folder of its own. Steam downloads what it needs "
                        + "again."));
                    break;
                }

                case Kind.Link:
                    examination.Notes.Add(new PlanNote(
                        PlanNoteSeverity.Information,
                        $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not "
                        + "delete through a link."));
                    examination.Declined.Add((path, LinkReason));
                    break;

                case Kind.Unrecognised:
                    examination.Notes.Add(new PlanNote(
                        PlanNoteSeverity.Information,
                        $"Leaving '{path}' alone: {UnrecognisedReason}"));
                    examination.Declined.Add((path, UnrecognisedReason));
                    break;

                // The index is named among the neighbours, with its own reason.
                case Kind.Index:
                    break;
            }
        }

        return examination;
    }

    /// <summary>
    /// One game's artwork as a target. Keyed by the application id, so a game kept stays kept whichever
    /// layout its artwork is in, and an older client's several files for one game are kept together.
    /// </summary>
    private static DeletionTarget Target(SteamApp app, string path, TargetKind kind, string reason) => new(
        path,
        reason,
        Kind: kind,
        Identity: app.Identity,
        Facets: app.Facets);

    /// <summary>
    /// The plan for a machine whose Steam install was not found, which is where the artwork is kept.
    /// The same three answers <see cref="SteamCacheProvider"/> gives for its cache there.
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
        _discovery.UnreachedInstallNote("the library artwork Steam keeps beside the program");

    /// <summary>What one planning pass found in the container.</summary>
    private sealed class Examination
    {
        public List<DeletionTarget> Targets { get; } = [];

        public List<(string Path, string Reason)> Declined { get; } = [];

        public List<PlanNote> Notes { get; } = [];
    }
}
