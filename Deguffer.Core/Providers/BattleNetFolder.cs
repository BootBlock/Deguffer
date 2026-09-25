using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Battle.net desktop launcher's folder under <c>%LOCALAPPDATA%</c>, and what the two providers
/// acting inside it both have to agree about.
///
/// <para>Shared rather than owned by one of them because §5.2 is a fact about the <em>directory</em>
/// and not about a provider, on <see cref="EpicLauncherSaved"/>'s reasoning. One folder holds the
/// launcher's own cache, its logs, its embedded browser's folder, its account data and a database
/// of what it has learnt, so "which children of this folder may go?" has one answer.</para>
///
/// <para><b>The cache and the logs are two providers because they are two tiers.</b> A plan carries
/// one tier. The cache is Tier 1, downloads the launcher fetches again on demand. A log is the
/// record of a session that will not happen again, which is Tier 3 and never pre-selected.</para>
///
/// <para><b>The embedded browser's folder is neither.</b> <c>BrowserCaches</c> is the Chromium
/// Embedded Framework's user-data folder, with the browser's sign-in state beside its caches, so
/// nothing removes it whole. <see cref="ChromiumCacheProvider"/> reaches the caches inside it through
/// its <see cref="ChromiumHost"/> row.</para>
///
/// <para><b>The machine-wide folders are left alone.</b> Blizzard's support article has players
/// delete the <c>Blizzard Entertainment</c> folder under <c>%PROGRAMDATA%</c> whole. That folder,
/// and the launcher's own <c>%PROGRAMDATA%\Battle.net</c> with the update agent's live state in
/// its <c>Agent</c> folder, are ones nobody has established the cost of removing. §5.2 makes both
/// Tier 4 until somebody does.</para>
/// </summary>
public static class BattleNetFolder
{
    /// <summary>The launcher's own directory under <c>%LOCALAPPDATA%</c>.</summary>
    private const string LauncherDirectory = "Battle.net";

    /// <summary>The launcher's own cache, which the Tier 1 row removes.</summary>
    public const string CacheDirectory = "Cache";

    /// <summary>The launcher's logs, which the Tier 3 row removes.</summary>
    public const string LogDirectory = "Logs";

    /// <summary>The embedded browser's user-data folder, which nothing here removes.</summary>
    private const string BrowserDirectory = "BrowserCaches";

    /// <summary>
    /// §5.3. The launcher holds its cache and the log it is writing open while it runs, so an
    /// access denial during a clean is the ordinary outcome rather than a fault.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["Battle.net"];

    /// <summary>
    /// §5.2 over the folder's children, and so the allow-list Explore reads for it. Everything not
    /// named is Tier 4 by construction: <c>Account</c> is the launcher's per-account data.
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        new ChildClassification(
            CacheDirectory,
            SafetyTier.RegenerableCache,
            "Data the launcher downloaded and saved so it would not fetch it twice. The launcher "
            + "downloads it again when it is next wanted."),
        new ChildClassification(
            LogDirectory,
            SafetyTier.UserData,
            "The launcher's own logs and its built-in browser's. The launcher writes a fresh one "
            + "each time it starts, and the ones removed are not written again."),
        new ChildClassification(
            BrowserDirectory,
            SafetyTier.DoNotTouch,
            "The launcher's built-in browser. Your sign-in to it is in there, so the folder itself "
            + "is never removed — only the caches inside it, on the Chromium application caches row."),
    ]);

    /// <summary>
    /// What §5.6 must show survived, named rather than left to an enumeration, since neither row
    /// enumerates this folder at all. The two files are named because nothing that classifies a
    /// directory would ever see one.
    /// </summary>
    private static readonly (string RelativePath, string Reason)[] ProtectedNames =
    [
        ("Account", "The launcher's data for each account that has signed in on this machine."),
        ("CachedData.db", "A database the launcher keeps. Nobody has established what is in it, so it is left alone."),
        (BrowserDirectory, "The launcher's built-in browser, with your sign-in to it."),
        (Path.Combine(BrowserDirectory, ChromiumLayout.EmbeddedFramework.IdentifyingFile),
            "The built-in browser's settings, and the key that decrypts its saved sign-in."),
        (CacheDirectory, "The launcher's cache, which has a row of its own."),
        (LogDirectory, "The launcher's logs, which have a row of their own."),
    ];

    /// <summary>The launcher's folder on <paramref name="environment"/>'s machine.</summary>
    public static string PathIn(IUserEnvironment environment) =>
        Path.Combine(environment.LocalAppData, LauncherDirectory);

    /// <summary>
    /// The folder as a <see cref="DeclaredRoot"/> with one location in it: the folder is never a
    /// target, and every other name above is asserted to survive. The location's own name is left
    /// out of the survivors, because it is the one thing the row removes.
    /// </summary>
    public static DeclaredRoot Declare(IUserEnvironment environment, DeclaredLocation location) => new(
        PathIn(environment),
        "The Battle.net launcher's own folder must survive — only the one folder this row names "
        + "inside it is removed.",
        RequiresElevation: false,
        [location],
        [.. ProtectedNames.Where(p => !p.RelativePath.Equals(location.RelativePath, StringComparison.OrdinalIgnoreCase))]);

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside, declared identically by both rows so that dropping
    /// either one leaves Explore unwilling to remove the launcher's account data.
    /// </summary>
    public static ToolRoot Root(IUserEnvironment environment) => ToolRoot.Of(
        PathIn(environment),
        "This is the Battle.net launcher's own folder. Your account data and the launcher's "
        + "database are in it, and Deguffer removes the cache and the logs from the Storage page, "
        + "where it knows which of them are which.",
        Children);
}
