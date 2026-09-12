using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where one edition of the Spotify desktop app keeps its streaming cache, its downloads and its
/// settings.
///
/// <para><b>Spotify documents none of these paths.</b> Its storage article names no folder on
/// Windows at all, so every path here was observed and reported by others rather than stated by the
/// vendor. <c>docs/cache-locations.md</c> carries the sources.</para>
///
/// <para><b>The two editions differ only in where things are, which is data.</b> The installer's
/// edition keeps the cache and the downloads side by side under <c>%LOCALAPPDATA%\Spotify</c>, and
/// its settings with the program under <c>%APPDATA%\Spotify</c>. The Microsoft Store edition is
/// packaged, so Windows gives it a folder under <c>%LOCALAPPDATA%\Packages</c>: the cache sits under
/// <c>LocalCache</c> there, and the downloads sit with the settings under <c>LocalState</c>. What
/// may go, and what must survive, is the same for both.</para>
/// </summary>
/// <param name="CacheFolder">
/// Spotify's own folder holding the streaming cache. A container, never a target, because the
/// installer's edition keeps the downloads in it too.
/// </param>
/// <param name="SettingsFolder">The folder holding Spotify's <c>prefs</c> and its per-account folders.</param>
/// <param name="OfflineStore">Where downloads go until the user moves them from Spotify's Settings page.</param>
/// <param name="Package">
/// The folder Windows gave the Store edition, or null for the installer's edition, which has none.
/// </param>
public sealed record SpotifyEdition(
    string CacheFolder,
    string SettingsFolder,
    string OfflineStore,
    string? Package)
{
    /// <summary>The streaming cache, and the one name this provider removes.</summary>
    public const string CacheName = "Data";

    /// <summary>The downloads, which are never offered.</summary>
    public const string OfflineStoreName = "Storage";

    /// <summary>Spotify's per-account folders.</summary>
    public const string AccountsName = "Users";

    /// <summary>
    /// The Store edition's package family. Matched exactly rather than by the <c>SpotifyAB</c>
    /// prefix, because a folder that merely starts the same way has not been identified as this
    /// package.
    /// </summary>
    public const string StorePackageFamily = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0";

    private const string SettingsFileName = "prefs";

    /// <summary>Spotify's record of what was downloaded, which sits beside the downloads.</summary>
    private const string OfflineIndexName = "offline.bnk";

    public string Cache => Path.Combine(CacheFolder, CacheName);

    /// <summary>
    /// The directory the provider declares. For the Store edition it is the package folder, so the
    /// walk down to the cache checks <c>LocalCache</c> and <c>Spotify</c> for a link as well.
    /// </summary>
    public string Root => Package ?? CacheFolder;

    public string SettingsFile => Path.Combine(SettingsFolder, SettingsFileName);

    public string OfflineIndex => Path.Combine(OfflineStoreParent, OfflineIndexName);

    public string OfflineStoreParent => Path.GetDirectoryName(OfflineStore)!;

    public string Accounts => Path.Combine(SettingsFolder, AccountsName);

    /// <summary>Both editions, the installer's first. A machine may have either, both or neither.</summary>
    public static IReadOnlyList<SpotifyEdition> In(IUserEnvironment environment)
    {
        var local = Path.Combine(environment.LocalAppData, "Spotify");
        var package = Path.Combine(environment.LocalAppData, "Packages", StorePackageFamily);
        var state = Path.Combine(package, "LocalState", "Spotify");

        return
        [
            new SpotifyEdition(
                local,
                Path.Combine(environment.RoamingAppData, "Spotify"),
                Path.Combine(local, OfflineStoreName),
                Package: null),
            new SpotifyEdition(
                Path.Combine(package, "LocalCache", "Spotify"),
                state,
                Path.Combine(state, OfflineStoreName),
                package),
        ];
    }
}
