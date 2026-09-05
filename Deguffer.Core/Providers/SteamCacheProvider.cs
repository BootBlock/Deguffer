using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The web cache the Steam client's embedded browser writes (472 MB in <c>htmlcache</c> alone on the
/// machine this was measured on, out of 513 MB for Steam's whole folder in the profile).
///
/// <para><b>Steam is two directories, and only one of them is in the profile.</b> The client renders
/// its store, library and overlay in an embedded Chromium, and keeps that browser's cache under
/// <c>%LOCALAPPDATA%\Steam</c> rather than beside the program. A second cache — the client's own
/// HTTP cache — sits in <c>appcache</c> under the install directory, which is not under the profile
/// at all and moves with whichever drive the user gave their game library. So the install directory
/// is <em>found</em> rather than assumed, and <see cref="SteamDiscovery"/> is what finds it.</para>
///
/// <para><b>The install directory is where §5.2 earns its keep, and the stakes are unusually
/// plain.</b> The same folder holds <c>steamapps</c>, which is every installed game and the
/// in-progress half of any download, and <c>userdata</c>, which is per-account settings, cloud saves
/// and screenshots. Nothing under either is ever reached: this provider names two paths outright and
/// enumerates neither root, so there is no enumeration through which an unnamed sibling could be
/// found. <see cref="DeclaredLocations"/> carries the naming, and the names that must survive are
/// declared beside the ones that may go, so a run produces evidence that a rule reaching into
/// Steam's folder did not reach the games.</para>
///
/// <para><b>Two things next to the cache are deliberately not offered.</b> <c>widevine</c> is a
/// content-decryption module Steam downloaded, not a cache, so it is unrecognised under §5.2 and
/// stays. <c>cefdata</c> is the embedded browser's working data, and what removing it costs was
/// never established — so it is named and left alone rather than guessed at. <c>appcache</c> keeps
/// Steam's own application and package indexes as files beside <c>httpcache</c>, and those are named
/// too: child classification enumerates directories, so a file in a container is never seen and
/// never asserted unless the provider names it.</para>
///
/// <para><b>§5.1 does not apply.</b> Steam ships no command-line switch that evicts either cache.
/// The client is reported to offer the same thing as a button under Settings, Web Browser, and that
/// report was not verified against a running client — but a button inside a running application is
/// not a route Deguffer can take either way, so path deletion is the only available method.</para>
/// </summary>
public sealed class SteamCacheProvider : CleanupProviderBase
{
    /// <summary>The embedded browser's cache, under Steam's folder in the profile.</summary>
    private const string HtmlCacheName = "htmlcache";

    /// <summary>Steam's own cache container in the install directory. A container, never a target.</summary>
    private const string AppCacheDirectory = "appcache";

    /// <summary>The client's HTTP cache, inside <see cref="AppCacheDirectory"/>.</summary>
    private const string HttpCacheName = "httpcache";

    private const string HtmlCacheReason =
        "Store, library and community pages the Steam client saved so it would not fetch the same "
        + "thing twice. It downloads them again when they are next shown.";

    private const string HttpCacheReason =
        "The Steam client's own HTTP cache, kept beside the program. The client refills it from "
        + "Valve's servers as it needs to.";

    private const string LocalRootReason =
        "This is Steam's own folder in your profile. Deguffer removes the browser cache inside it "
        + "and nothing else.";

    private readonly SteamDiscovery _discovery;
    private IReadOnlyList<DeclaredRoot>? _roots;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    public SteamCacheProvider(
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

    public override string Id => "steam";

    public override string Name => "Steam web cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The Steam client fetches store, library and community pages from the network instead of "
        + "from disk for a while, so they draw more slowly the first time. It may ask you to sign "
        + "in again to the pages it shows inside the client. Your installed games, any download in "
        + "progress, your cloud saves and your settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Steam client",
        Publisher = "Valve",
        Purpose = "Steam draws its store, library and overlay in a browser built into the client, "
            + "and that browser saves what it downloads. The cache lives in your profile rather "
            + "than with the program, and the client keeps a second one of its own beside the "
            + "program.",
        Recommendation = "Deguffer removes the two caches by name and nothing else. It never goes "
            + "near your installed games, a download in progress, your Workshop content or your "
            + "cloud saves, and it asks Windows where Steam is rather than assuming.",
    };

    /// <summary>
    /// What this provider names, root by root. Exposed so tests can assert that neither Steam
    /// directory is a target and that the games are asserted rather than merely omitted.
    /// </summary>
    public IReadOnlyList<DeclaredRoot> Roots => _roots ??= Declare();

    /// <summary>§5.3. The client and its browser process hold both caches open while Steam runs.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["steam", "steamwebhelper"];

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
        Task.FromResult(DeclaredPaths().Any(LongPath.DirectoryExists) || InstallUnreached() is not null);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var scan = DeclaredLocations.Examine(Roots, ct);
        var unreached = InstallUnreached();

        if (scan.FoundNothing)
        {
            return unreached is { } why
                ? UnexaminedPlan(why)
                : EmptyPlan("The Steam client is keeping no web cache on this machine.");
        }

        var notes = new List<PlanNote>(scan.Notes);

        if (unreached is { } sentence)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, sentence));
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
            WasNotExamined = scan.Targets.Count == 0 && (scan.Declined.Count > 0 || unreached is not null),
        };
    }

    /// <summary>
    /// The sentence owed about an install directory this machine gave no usable answer for, or null
    /// when it was found — or when there is no Steam in this profile to be missing one.
    ///
    /// <para>Gated on the profile folder because the alternative is to tell somebody who has never
    /// installed Steam that Deguffer could not find it.</para>
    /// </summary>
    private string? InstallUnreached()
    {
        if (_discovery.Install.Root is not null || !LongPath.DirectoryExists(_discovery.LocalRoot))
        {
            return null;
        }

        return _discovery.Install.UnmarkedRoot is { } unmarked
            ? $"Windows records Steam as installed in '{unmarked}', but the Steam program is not "
                + "there. Deguffer did not look inside it, so the cache Steam keeps beside the "
                + "program was neither cleared nor ruled out."
            : "Deguffer could not work out where Steam is installed, so it did not look at the "
                + "cache Steam keeps beside the program. That cache was neither cleared nor ruled "
                + "out.";
    }

    /// <summary>
    /// The two locations, and everything beside them that §5.6 must assert survived.
    ///
    /// <para><c>steamapps</c> is named four times over — itself, the games under it, the Workshop
    /// content and the in-progress half of a download — because an assertion that the folder
    /// survived would pass with every game inside it gone. It is the same reason Firefox names
    /// <c>logins.json</c> rather than the profile that holds it.</para>
    ///
    /// <para><b>Neither root needs administrator rights.</b> Steam's installer grants this account
    /// write access to the install directory so the client can update itself, which is why a
    /// declaration that is otherwise true of anything under Program Files is false here. It was
    /// reasoned from how Steam updates rather than measured, and the cost of being wrong is an
    /// access denial the executor reports, not a silent partial removal.</para>
    /// </summary>
    private IReadOnlyList<DeclaredRoot> Declare()
    {
        var roots = new List<DeclaredRoot>
        {
            new(
                _discovery.LocalRoot,
                LocalRootReason,
                RequiresElevation: false,
                [new DeclaredLocation(HtmlCacheName, HtmlCacheReason)],
                [
                    ("cefdata", "The embedded browser's working data. It sits beside the cache and "
                        + "nobody has established what removing it costs."),
                    ("widevine", "The content-decryption module Steam downloaded so protected video "
                        + "will play. It is downloaded software rather than a cache."),
                    // A file, and the NVIDIA 'accounts' lesson: a child set classifies directories,
                    // so a file in a root a provider reaches into is never asserted unless it is
                    // named. Found by looking at a real Steam folder rather than by reasoning.
                    ("local.vdf", "The Steam client's settings for this computer."),
                ]),
        };

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
                        "Artwork Steam downloaded for your library. It is a cache, but how much it "
                        + "costs to fetch again was never established, so it is left alone."),
                ]));
        }

        return roots;
    }

    private IReadOnlyList<ToolRoot> DeclareToolRoots()
    {
        var roots = new List<ToolRoot>
        {
            ToolRoot.Of(
                _discovery.LocalRoot,
                LocalRootReason,
                new DisposableChildSet(
                [
                    new ChildClassification(HtmlCacheName, SafetyTier.RegenerableCache, HtmlCacheReason),
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
                    new ChildClassification(
                        "librarycache",
                        SafetyTier.DoNotTouch,
                        "Artwork Steam downloaded for your library. Deguffer does not offer it, "
                        + "because what fetching it again costs was never established."),
                ])));
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
