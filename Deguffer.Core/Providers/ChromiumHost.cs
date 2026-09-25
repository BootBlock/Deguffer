using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where one application that hosts the Chromium engine keeps its user-data folder, as its vendor
/// documents it or as it was measured: a Chromium-based browser, or a launcher built on the Chromium
/// Embedded Framework.
///
/// <para>A declaration rather than a deeper walk. Every such host keeps that folder two or three
/// levels below an application-data root, under a vendor or product directory, where the
/// one-level walk in <see cref="ChromiumUserDataDiscovery"/> never looks. Walking two levels of both
/// roots would multiply the candidates that walk's one file-existence check exists to keep few, and
/// a recursive search for <c>Local State</c> would identify folders nobody declared. So each host
/// is named here, and a row only says <em>where</em> to look: the folder still has to hold its
/// <see cref="ChromiumLayout.IdentifyingFile"/> before anything inside it is examined.
/// </para>
///
/// <para>A wrong row therefore identifies nothing, which is the safe direction to be wrong in.</para>
/// </summary>
/// <param name="Name">The product's name, which is what the plan lists its caches under.</param>
/// <param name="Area">The application-data tier the folder sits in.</param>
/// <param name="RelativePath">The folder below that tier, vendor directory first.</param>
/// <param name="ProcessName">
/// The host's process, for §5.3's warning. Declared because the folder's own name is
/// <c>User Data</c> for almost every browser, which is nobody's process.
/// </param>
public sealed record ChromiumHost(
    string Name,
    ProfileArea Area,
    string RelativePath,
    string ProcessName)
{
    /// <summary>
    /// Which file identifies the folder and how its profiles are found. A browser's, unless the row
    /// says otherwise.
    /// </summary>
    public ChromiumLayout Layout { get; init; } = ChromiumLayout.Browser;

    /// <summary>
    /// The hosts Deguffer looks for, with every release channel that keeps a folder of its own.
    /// Chromium's own documentation gives the Google rows. The other browsers follow the same
    /// vendor-then-product shape, except Opera, which keeps its settings in the roaming tier and the
    /// folder itself as its only profile.
    ///
    /// <para>Battle.net keeps its engine's folder a level below its own, beside the launcher's
    /// account data, and marks it with the framework's <c>LocalPrefs.json</c> rather than
    /// <c>Local State</c>. Its <c>Cache</c> and <c>Logs</c> are the launcher's own, not the
    /// engine's, and are <see cref="BattleNetCacheProvider"/>'s and
    /// <see cref="BattleNetLogProvider"/>'s.</para>
    /// </summary>
    public static readonly IReadOnlyList<ChromiumHost> Declared =
    [
        new("Google Chrome", ProfileArea.LocalAppData, @"Google\Chrome\User Data", "chrome"),
        new("Google Chrome Beta", ProfileArea.LocalAppData, @"Google\Chrome Beta\User Data", "chrome"),
        new("Google Chrome Dev", ProfileArea.LocalAppData, @"Google\Chrome Dev\User Data", "chrome"),
        new("Google Chrome Canary", ProfileArea.LocalAppData, @"Google\Chrome SxS\User Data", "chrome"),
        new("Google Chrome for Testing", ProfileArea.LocalAppData, @"Google\Chrome for Testing\User Data", "chrome"),
        new("Chromium", ProfileArea.LocalAppData, @"Chromium\User Data", "chrome"),
        new("Microsoft Edge", ProfileArea.LocalAppData, @"Microsoft\Edge\User Data", "msedge"),
        new("Microsoft Edge Beta", ProfileArea.LocalAppData, @"Microsoft\Edge Beta\User Data", "msedge"),
        new("Microsoft Edge Dev", ProfileArea.LocalAppData, @"Microsoft\Edge Dev\User Data", "msedge"),
        new("Microsoft Edge Canary", ProfileArea.LocalAppData, @"Microsoft\Edge SxS\User Data", "msedge"),
        new("Brave", ProfileArea.LocalAppData, @"BraveSoftware\Brave-Browser\User Data", "brave"),
        new("Brave Beta", ProfileArea.LocalAppData, @"BraveSoftware\Brave-Browser-Beta\User Data", "brave"),
        new("Brave Nightly", ProfileArea.LocalAppData, @"BraveSoftware\Brave-Browser-Nightly\User Data", "brave"),
        new("Vivaldi", ProfileArea.LocalAppData, @"Vivaldi\User Data", "vivaldi"),
        new("Opera", ProfileArea.RoamingAppData, @"Opera Software\Opera Stable", "opera"),
        new("Opera GX", ProfileArea.RoamingAppData, @"Opera Software\Opera GX Stable", "opera"),
        new("Battle.net", ProfileArea.LocalAppData, @"Battle.net\BrowserCaches", "Battle.net")
        {
            Layout = ChromiumLayout.EmbeddedFramework,
        },
    ];

    /// <summary>
    /// The application-data tier on <paramref name="environment"/>'s machine and the folder below it,
    /// or null where the tier could not be located.
    /// </summary>
    public (string Root, string UserData)? PathIn(IUserEnvironment environment) =>
        Area.In(environment) is { } root ? (root, Path.Combine(root, RelativePath)) : null;
}
