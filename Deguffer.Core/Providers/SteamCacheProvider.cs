using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Steam client's own HTTP cache, in <c>appcache\httpcache</c> under the install directory.
///
/// <para><b>The embedded browser's folder is not this row's, and it is never removed whole.</b> The
/// client renders its store, library and overlay in an embedded Chromium whose folder is
/// <c>%LOCALAPPDATA%\Steam\htmlcache</c>. Despite its name, that folder is a browser user-data folder:
/// on the measured machine it held <c>Local State</c> and a <c>Default</c> profile with
/// <c>Login Data</c>, <c>Web Data</c> and <c>Network\Cookies</c> beside the caches. This row used to
/// remove it whole, which signed the user out of the client's store and community pages. It now
/// follows the rule <see cref="EpicLauncherWebCacheProvider"/> and <see cref="BattleNetFolder"/>
/// state for the same kind of folder: <see cref="ChromiumHost"/> declares it, so
/// <see cref="ChromiumCacheProvider"/> takes the caches inside it (120 MB of the 129 MB measured) and
/// asserts the credential files survived, and the folder is Tier 4 here.</para>
///
/// <para><b>The install directory is not under the profile.</b> It moves with whichever drive the
/// user gave their game library, so it is <em>found</em> rather than assumed, and
/// <see cref="SteamDiscovery"/> is what finds it.</para>
///
/// <para><b>The install directory is where §5.2 earns its keep, and the stakes are unusually
/// plain.</b> The same folder holds <c>steamapps</c>, which is every installed game and the
/// in-progress half of any download, and <c>userdata</c>, which is per-account settings, cloud saves
/// and screenshots. Nothing under either is ever reached: this provider names one path outright and
/// never enumerates the root, so there is no enumeration through which an unnamed sibling could be
/// found. <see cref="DeclaredLocations"/> carries the naming, and the names that must survive are
/// declared beside the ones that may go, so a run produces evidence that a rule reaching into
/// Steam's folder did not reach the games.</para>
///
/// <para><b>Three things in the profile folder are recognised and then deliberately not offered.</b>
/// <c>htmlcache</c> is the embedded browser's folder, as above. <c>widevine</c> is a
/// content-decryption module Steam downloaded rather than a cache. <c>cefdata</c> is the embedded
/// browser's working data, and what removing it costs was never established. Each is declared at
/// Tier 4 in <see cref="ToolRoots"/> rather than merely omitted, so the refusal carries its own
/// sentence instead of the generic "not recognised" one. <c>appcache\librarycache</c> is the
/// library artwork, which <see cref="SteamLibraryArtworkProvider"/> offers game by game, so here it
/// is only a neighbour that must survive. <c>appcache</c> also keeps Steam's own application and package indexes as files
/// beside <c>httpcache</c>, and those are named too: child classification enumerates directories, so
/// a file in a container is never seen and never asserted unless the provider names it.</para>
///
/// <para><b>§5.1 does not apply.</b> Steam ships no command-line switch that evicts the cache.
/// The client's Settings, Downloads, Clear Download Cache is reported to clear <c>appcache</c>, and
/// that report was not verified against a running client — but a button inside a running
/// application is not a route Deguffer can take either way, so path deletion is the only available
/// method.</para>
/// </summary>
public sealed class SteamCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// The embedded browser's user-data folder, under Steam's folder in the profile. Never a target
    /// of this row: <see cref="ChromiumHost"/> declares it for the Chromium rows.
    /// </summary>
    private const string HtmlCacheName = "htmlcache";

    /// <summary>Steam's own cache container in the install directory. A container, never a target.</summary>
    private const string AppCacheDirectory = "appcache";

    /// <summary>The client's HTTP cache, inside <see cref="AppCacheDirectory"/>.</summary>
    private const string HttpCacheName = "httpcache";

    private const string HttpCacheReason =
        "The Steam client's own HTTP cache, kept beside the program. The client refills it from "
        + "Valve's servers as it needs to.";

    private readonly SteamDiscovery _discovery;
    private IReadOnlyList<DeclaredRoot>? _roots;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    public SteamCacheProvider(
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

    public override string Id => "steam";

    public override string Name => "Steam HTTP cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The Steam client fetches what it had cached from Valve's servers again as it needs it, so "
        + "some of what it shows loads more slowly the first time. Your installed games, any "
        + "download in progress, your cloud saves, your settings and your sign-in are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Steam client",
        Publisher = "Valve",
        Purpose = "The Steam client keeps an HTTP cache of its own beside the program, in the "
            + "folder Steam is installed in. The browser built into the client keeps its caches "
            + "in your profile, beside your sign-in to the store, and the Chromium application "
            + "caches row removes those.",
        Recommendation = "Deguffer removes the one cache by name and nothing else. It never goes "
            + "near your installed games, a download in progress, your Workshop content or your "
            + "cloud saves, and it asks Windows where Steam is rather than assuming.",
    };

    /// <summary>
    /// What this provider names. Exposed so tests can assert that the install directory is never a
    /// target and that the games are asserted rather than merely omitted.
    /// </summary>
    public IReadOnlyList<DeclaredRoot> Roots => _roots ??= Declare();

    /// <summary>§5.3. The client holds its cache open while Steam runs.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => SteamDiscovery.ProcessNames;

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside. Three roots rather than two, because a
    /// <see cref="ToolRoot"/> classifies a directory's <em>immediate</em> children and the HTTP
    /// cache is a level down: the install directory recognises nothing at all, and <c>appcache</c>
    /// under it recognises the one child that may go.
    ///
    /// <para>Without these, a user could delete <c>steamapps</c> out of the size picture while the
    /// Storage page was carefully leaving it alone. Windows' own Program Files is already refused
    /// there, but a Steam library is put on a second drive precisely so that it is not.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => _toolRoots ??= DeclareToolRoots();

    public override void InvalidateCaches()
    {
        _discovery.Invalidate();
        _roots = null;
        _toolRoots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is a declared cache actually on disk, or a Steam in the profile whose install
    /// directory could not be reached.
    ///
    /// <para>The second half is there because the planner never asks an absent provider for a plan,
    /// and <see cref="BuildPlanAsync"/>'s sentence about an install it could not find would then be
    /// unreachable. The row would read "Not installed" about a Steam that is installed, which is a
    /// stronger untruth than the "Already clear" it would otherwise be — the same correction the
    /// Firefox register forced.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DeclaredPaths().Any(LongPath.DirectoryMayExist) || InstallUnreached() is not null);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var scan = DeclaredLocations.Examine(Roots, ct);
        var unreached = InstallUnreached();
        var refusedInstall = _discovery.Install.UnreachedRoot;

        if (scan.FoundNothing)
        {
            if (unreached is null)
            {
                return EmptyPlan("The Steam client is keeping no web cache on this machine.");
            }

            return refusedInstall is null
                ? UnexaminedPlan(unreached.Message)
                : UnreadableRootPlan(refusedInstall) with { Notes = [unreached] };
        }

        var notes = new List<PlanNote>(scan.Notes);

        if (unreached is { } sentence)
        {
            notes.Add(sentence);
        }

        var (steps, measured) = await PlanDeletionsAsync(scan.Targets, keep, ct).ConfigureAwait(false);

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
            ProtectedPaths = Protect([.. scan.Protected]),
            Notes = notes,
            Fallback = measured.Fallback,
            // An install directory nobody could reach counts here for the same reason a declined
            // link does: nothing was removed and something was never looked at, so the shell must
            // not call the row clear.
            // A refused install directory is Windows' answer rather than Deguffer's decision, so it
            // is the other flag.
            WasNotExamined = scan.Targets.Count == 0
                && (scan.Declined.Count > 0 || (unreached is not null && refusedInstall is null)),
            HasUnreadableRoot = scan.CouldNotBeReached || refusedInstall is not null,
        };
    }

    /// <summary>
    /// The sentence owed about an install directory this machine gave no usable answer for. See
    /// <see cref="SteamDiscovery.UnreachedInstallNote"/>.
    /// </summary>
    private PlanNote? InstallUnreached() =>
        _discovery.UnreachedInstallNote("the cache Steam keeps beside the program");

    /// <summary>
    /// The one location, and everything beside it that §5.6 must assert survived. None where the
    /// install was not found: Steam's folder in the profile holds nothing this row removes.
    ///
    /// <para><c>steamapps</c> is named four times over — itself, the games under it, the Workshop
    /// content and the in-progress half of a download — because an assertion that the folder
    /// survived would pass with every game inside it gone. It is the same reason Firefox names
    /// <c>logins.json</c> rather than the profile that holds it.</para>
    ///
    /// <para><b>The root needs no administrator rights.</b> Steam's installer grants this account
    /// write access to the install directory so the client can update itself, which is why a
    /// declaration that is otherwise true of anything under Program Files is false here. It was
    /// reasoned from how Steam updates rather than measured, and the cost of being wrong is an
    /// access denial the executor reports, not a silent partial removal.</para>
    /// </summary>
    private IReadOnlyList<DeclaredRoot> Declare()
    {
        var roots = new List<DeclaredRoot>();

        if (_discovery.Install.Root is { } install)
        {
            roots.Add(new DeclaredRoot(
                install,
                "This is where Steam itself is installed, with your games beside it. Deguffer "
                + "removes one named cache from inside it and nothing else.",
                RequiresElevation: false,
                [new DeclaredLocation(Path.Combine(AppCacheDirectory, HttpCacheName), HttpCacheReason)],
                [
                    ("steamapps", "Your installed games."),
                    (Path.Combine("steamapps", "common"), "The games themselves, on disk."),
                    (Path.Combine("steamapps", "downloading"),
                        "The half-downloaded part of an update. Removing it restarts the download."),
                    (Path.Combine("steamapps", "workshop"), "Workshop content you subscribed to."),
                    ("userdata", "Your Steam settings, cloud saves and screenshots, per account."),
                    ("config", "Steam's own configuration, including who is signed in on this computer."),
                    (Path.Combine(AppCacheDirectory, "appinfo.vdf"),
                        "Steam's index of the applications it knows about. It sits beside the cache "
                        + "and is not one."),
                    (Path.Combine(AppCacheDirectory, "packageinfo.vdf"),
                        "Steam's index of the packages it knows about. It sits beside the cache and "
                        + "is not one."),
                    (Path.Combine(AppCacheDirectory, "librarycache"),
                        "Artwork Steam downloaded for your library, which the Steam library artwork row "
                        + "offers game by game."),
                ]));
        }

        return roots;
    }

    private IReadOnlyList<ToolRoot> DeclareToolRoots()
    {
        var roots = new List<ToolRoot>
        {
            // It offers nothing, and is still declared: without it, Explore would let the user take
            // the browser folder, and the sign-in inside it, whole.
            ToolRoot.Of(
                _discovery.LocalRoot,
                "This is Steam's own folder in your profile. The client's settings for this computer "
                + "and its built-in browser, with your sign-in to the store, are in it. Deguffer "
                + "removes only the caches the Chromium rows recognise inside that browser's folder.",
                new DisposableChildSet(
                [
                    new ChildClassification(
                        HtmlCacheName,
                        SafetyTier.DoNotTouch,
                        "The client's built-in browser. Your sign-in to the store and community pages "
                        + "is in there, so the folder itself is never removed — only what the "
                        + "Chromium rows recognise inside it."),
                    new ChildClassification(
                        "cefdata",
                        SafetyTier.DoNotTouch,
                        "The embedded browser's working data, which is not a cache Deguffer knows "
                        + "how to account for."),
                    new ChildClassification(
                        "widevine",
                        SafetyTier.DoNotTouch,
                        "Downloaded software that lets protected video play, rather than a cache."),
                ])),
        };

        if (_discovery.Install.Root is { } install)
        {
            roots.Add(ToolRoot.Of(
                install,
                "This is where Steam itself is installed. Your games, your cloud saves and Steam's "
                + "own configuration are in here, and Deguffer removes none of them.",
                new DisposableChildSet(
                [
                    new ChildClassification(
                        "steamapps",
                        SafetyTier.DoNotTouch,
                        "Your installed games, your Workshop content, and the half-downloaded part "
                        + "of any update."),
                    new ChildClassification(
                        "userdata",
                        SafetyTier.DoNotTouch,
                        "Your Steam settings, cloud saves and screenshots."),
                    new ChildClassification(
                        "config",
                        SafetyTier.DoNotTouch,
                        "Steam's own configuration, including who is signed in on this computer."),
                ])));

            roots.Add(ToolRoot.Of(
                Path.Combine(install, AppCacheDirectory),
                "This is Steam's own cache folder, and it holds the indexes the client works from "
                + "as well. Deguffer removes the HTTP cache inside it and nothing else.",
                new DisposableChildSet(
                [
                    new ChildClassification(HttpCacheName, SafetyTier.RegenerableCache, HttpCacheReason),
                    // A container, never a target: the games' folders inside it are recognised by
                    // the root SteamLibraryArtworkProvider declares on it.
                    new ChildClassification(
                        "librarycache",
                        SafetyTier.DoNotTouch,
                        "The folder Steam keeps every game's library artwork in, with its index of that "
                        + "artwork. Deguffer removes the artwork for single games inside it, never the "
                        + "folder."),
                ])));
        }

        // Recognising nothing, because nothing established what is in there, and Steam's own record
        // says your games are. A declaration only ever narrows what Explore allows.
        if (_discovery.Install.UnreachedRoot is { } unreached)
        {
            roots.Add(new ToolRoot(
                unreached,
                "Windows records Steam as installed here, and would not say what is in this folder. Your "
                + "games, your cloud saves and Steam's own configuration may be in here, so Deguffer "
                + "leaves all of it alone.",
                static _ => false));
        }

        return roots;
    }

    /// <summary>
    /// Every path this provider could ever target, by declaration rather than by enumeration — so
    /// answering "is there anything here?" costs one existence check each and can never reach
    /// anything the table does not name.
    /// </summary>
    private IEnumerable<string> DeclaredPaths() =>
        from root in Roots
        from location in root.Locations
        select Path.Combine(root.Path, location.RelativePath);
}
