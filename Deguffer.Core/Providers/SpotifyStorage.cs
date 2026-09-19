using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One edition of Spotify, and what its settings file says.</summary>
public sealed record SpotifyInstall(SpotifyEdition Edition, SpotifySettings Settings)
{
    /// <summary>
    /// Whether this edition may have left either of its folders on this machine.
    ///
    /// <para>It decides only which folders §5.6 asserts survived, so a refusal reads as "may be
    /// there": §5.6 records a folder Windows would not describe as a refusal and checks it again
    /// after the run. See <see cref="PathPresence"/>.</para>
    /// </summary>
    public bool IsInstalled =>
        LongPath.DirectoryMayExist(Edition.CacheFolder) || LongPath.DirectoryMayExist(Edition.SettingsFolder);
}

/// <summary>
/// What every edition of Spotify on this machine says about where its storage is, and so which
/// caches may be offered.
///
/// <para><b>Spotify's Settings page moves its storage, and nobody can say which store moves.</b>
/// Spotify's help calls what moves its cache. The client labels it offline storage, and Spotify's
/// moderators say the cache cannot be moved at all. So a moved location is treated as though it
/// could be either. Nothing in it is ever measured or removed, and a cache it overlaps is never
/// offered, because downloaded music may be in there.</para>
///
/// <para><b>One edition's settings can withhold the other edition's cache.</b> A location is a
/// path, and nothing stops the installer's edition from being pointed inside the Store edition's
/// folder. So every location is checked against every cache, and a settings file that cannot be
/// read withholds every cache rather than its own edition's alone. Both editions on one machine is
/// rare, which is what makes the stricter rule cheap.</para>
///
/// <para>A record so that each answer is computed from <see cref="Installs"/> when asked. There are
/// two editions and a few locations, so there is nothing worth storing.</para>
/// </summary>
public sealed record SpotifyStorage(IReadOnlyList<SpotifyInstall> Installs)
{
    public static SpotifyStorage Find(IUserEnvironment environment) =>
        new([
            .. SpotifyEdition.In(environment).Select(
                edition => new SpotifyInstall(edition, SpotifySettings.Read(edition.SettingsFile))),
        ]);

    /// <summary>Every storage location any settings file names, once each.</summary>
    public IReadOnlyList<string> Locations =>
        [.. Installs.SelectMany(i => i.Settings.Locations).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The first settings file that could not say where the storage is, or null.</summary>
    public SpotifySettings? Unsettled => Installs.Select(i => i.Settings).FirstOrDefault(s => !s.IsSettled);

    /// <summary>
    /// The locations that are not where an edition keeps downloads by default. Each one is owed a
    /// sentence, a survival check and a refusal in Explore, whether or not it overlaps a cache and
    /// whether or not that cache is on disk.
    /// </summary>
    public IReadOnlyList<string> Moved =>
    [
        .. Locations.Where(location => !Installs.Any(
            i => location.Equals(i.Edition.OfflineStore, StringComparison.OrdinalIgnoreCase))),
    ];

    /// <summary>The locations that are the cache, sit inside it, or hold it.</summary>
    public IReadOnlyList<string> Overlapping(SpotifyEdition edition) =>
        [.. Locations.Where(location => Overlaps(location, edition.Cache))];

    /// <summary>
    /// Whether <paramref name="edition"/>'s cache may be offered: every settings file was read, and
    /// no location any of them names overlaps it.
    /// </summary>
    public bool MayOffer(SpotifyEdition edition) => Unsettled is null && Overlapping(edition).Count == 0;

    private static bool Overlaps(string location, string cache) =>
        LongPath.Contains(location, cache) || LongPath.Contains(cache, location);
}
