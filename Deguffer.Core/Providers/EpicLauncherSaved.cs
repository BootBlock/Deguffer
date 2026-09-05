using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Epic Games launcher's <c>Saved</c> folder, and what the two providers acting inside it both
/// have to agree about.
///
/// <para>Shared rather than owned by one of them because §5.2 is a fact about the <em>directory</em>
/// and not about a provider. One folder holds the launcher's storefront caches, its logs, its crash
/// reports, its settings and its saved state, so "which children of this folder may go?" has one
/// answer — and two copies of that answer is one copy that gets changed.</para>
///
/// <para><b>The web caches and the logs are two providers because they are two tiers.</b> A plan
/// carries one tier, and §7 derives the confirmation from it: the storefront caches are Tier 1 and
/// pre-selected, and a crash report is the record of an event that will not happen again to order,
/// which is Tier 3 and never pre-selected. One provider over both would have had to give the whole
/// row the stricter tier, which would hold 343 MB of plain web cache behind the typed phrase — or
/// the looser one, which would pre-select somebody's only copy of a crash trace. Splitting them is
/// the same call <see cref="WindowsServicingLogProvider"/> made against
/// <see cref="CrashDumpProvider"/>, for the same reason.</para>
/// </summary>
public static partial class EpicLauncherSaved
{
    /// <summary>The launcher's own directory under <c>%LOCALAPPDATA%</c>.</summary>
    private const string LauncherDirectory = "EpicGamesLauncher";

    /// <summary>The folder inside it that everything below is relative to.</summary>
    private const string SavedDirectory = "Saved";

    /// <summary>
    /// §5.3. The launcher holds its embedded browser's cache and its own log open while it runs, so
    /// an access denial during a clean is the ordinary outcome rather than a fault.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["EpicGamesLauncher"];

    /// <summary>
    /// The two directories that hold the launcher's own diagnostic output. Tier 3 on
    /// <see cref="CrashDumpProvider"/>'s reasoning: what gets re-created here is the next log, never
    /// the ones removed, and somebody halfway through a support ticket has the only copy.
    ///
    /// <para>This is also the allow-list the <c>Saved</c> folder is declared with, so it is what
    /// decides that <c>Config</c>, <c>Data</c>, <c>Saves</c> and <c>UserVaultSettings</c> are Tier 4
    /// by construction — settings and saved state, sitting in the same directory listing as the
    /// caches. §5.2 in one folder.</para>
    /// </summary>
    public static readonly DisposableChildSet Diagnostics = new(
    [
        new ChildClassification(
            "Crashes",
            SafetyTier.UserData,
            "Crash reports the launcher gathered when it failed. Each one is the record of a single "
            + "failure and nothing re-creates it."),
        new ChildClassification(
            "Logs",
            SafetyTier.UserData,
            "The launcher's and the updater's own logs. The launcher writes a fresh one each time it "
            + "starts, and the ones removed are not written again."),
    ]);

    /// <summary>
    /// What §5.6 must show survived, named rather than left to the enumeration.
    ///
    /// <para>Named for the reason Chromium's <c>Login Data</c> and NVIDIA's <c>accounts</c> are:
    /// child classification enumerates directories, so anything that turns out to be a file is never
    /// seen, never classified and never asserted unless a provider names it. These four are the
    /// entire reason the <c>Saved</c> folder cannot simply be deleted, and an assertion that the
    /// folder itself survived would pass with every one of them gone.</para>
    /// </summary>
    public static readonly (string Name, string Reason)[] ProtectedNames =
    [
        ("Config", "The launcher's settings, including which library folders you have added."),
        ("Data", "The launcher's own working state."),
        ("Saves", "Saved game data the launcher keeps in the cloud on your behalf."),
        ("UserVaultSettings", "Your settings for the vault the launcher stores purchases in."),
    ];

    /// <summary>
    /// A web cache folder, whose name carries the engine build the launcher was updated to. Epic's
    /// own support article names <c>webcache</c>, <c>webcache_4147</c> and <c>webcache_4430</c>, and
    /// the numbered suffix is why this is a pattern: each update renames the folder and leaves the
    /// old one behind.
    ///
    /// <para>An allow-list still, on <see cref="PlaywrightBrowsersProvider"/>'s pattern — a known
    /// word <em>and</em> a number. <c>webcache_backup</c> is not one of these, and neither is
    /// anything else that merely starts with the word.</para>
    ///
    /// <para>Anchored with <c>\z</c> rather than <c>$</c>: <c>$</c> also matches before a trailing
    /// newline, and a check deciding whether a directory may be deleted should admit no such
    /// reading.</para>
    /// </summary>
    [GeneratedRegex(@"\Awebcache(_[0-9]+)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex WebCacheDirectory();

    /// <summary>
    /// §5.2 over the <c>Saved</c> folder's children: the two diagnostic folders, then the web cache
    /// folders, then everything else at Tier 4 by construction.
    ///
    /// <para>A web cache folder is named rather than left to the generic "not a recognised
    /// disposable item" sentence, on <see cref="ChromiumCacheProvider"/>'s reasoning about its own
    /// containers: that wording would be misleading here, because the folder really is left standing
    /// while <see cref="EpicLauncherWebCacheProvider"/> really is removing caches from inside it.
    /// The tier is the same either way — nothing removes one of these whole.</para>
    /// </summary>
    public static ChildClassification Classify(string name)
    {
        var known = Diagnostics.Classify(name);

        return known.Tier.IsOfferable() || !WebCacheDirectory().IsMatch(name)
            ? known
            : new ChildClassification(
                name,
                SafetyTier.DoNotTouch,
                "The store's browser folder. Your sign-in and the store's saved data are in it, so "
                + "the folder itself is never removed — only the caches inside it, on the launcher's "
                + "web cache row.");
    }

    /// <summary>The launcher's own folder, which the <c>Saved</c> folder is built under.</summary>
    public static string LauncherRoot(IUserEnvironment environment) =>
        Path.Combine(environment.LocalAppData, LauncherDirectory);

    /// <summary>Where both providers work.</summary>
    public static string PathIn(IUserEnvironment environment) =>
        Path.Combine(LauncherRoot(environment), SavedDirectory);

    /// <summary>
    /// The path is assembled from <c>%LOCALAPPDATA%</c> and two constants rather than enumerated, so
    /// every segment of it is checked for a link before anything below it is planned. See
    /// <see cref="DerivedPath"/> for why the last segment alone is not enough.
    /// </summary>
    public static string? FirstLinkTo(IUserEnvironment environment) =>
        DerivedPath.FirstLinkBetween(environment.LocalAppData, PathIn(environment));

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: the <c>Saved</c> folder itself is never a target,
    /// and below it only the two diagnostic folders are recognised.
    ///
    /// <para>Declared by both providers, identically. That is redundant on a machine where both are
    /// registered and deliberate anyway: a declaration that existed only on one of them would leave
    /// Explore willing to remove somebody's launcher settings the moment the other provider was the
    /// one dropped.</para>
    ///
    /// <para><b>A web cache folder is not recognised here, and that is not an oversight.</b> The
    /// storefront's sign-in cookies and web storage sit inside it beside the caches, so nothing in
    /// Deguffer removes one whole — <see cref="EpicLauncherWebCacheProvider"/> reaches the caches
    /// inside it and declares a root of its own at that level.</para>
    /// </summary>
    public static ToolRoot Root(IUserEnvironment environment) => ToolRoot.Of(
        PathIn(environment),
        "This is the Epic Games launcher's own folder. Your launcher settings, your cloud saves and "
        + "your vault settings are in it, and Deguffer removes the caches and logs from the Storage "
        + "page, where it knows which of them are which.",
        Diagnostics);
}
