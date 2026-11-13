using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the <c>yarn</c> on the PATH says about the global folder of Yarn 2 and later (Berry), from
/// Deguffer's own folder, and the facts about that folder that hold whichever Yarn is asked.
/// Every behaviour here is read from yarnpkg/berry at @yarnpkg/cli/4.18.1
/// (<c>75ebe063092f86e47e4afeaf1ae4bdf24402874b</c>), 3.8.7 and 2.4.3.
/// </summary>
/// <param name="Version">Which Yarn the <c>yarn</c> on the PATH runs (<see cref="YarnVersion"/>).</param>
/// <param name="Settings">What Berry's settings say, or null where it is not Berry and was not asked.</param>
internal sealed partial record YarnBerryCache(YarnVersion Version, YarnBerrySettings? Settings)
{
    /// <summary>
    /// Berry's global folder where nothing moves it: <c>%LOCALAPPDATA%\Yarn\Berry</c> on Windows, in
    /// every Berry release
    /// (<a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/yarnpkg-core/sources/folderUtils.ts#L4-L20">folderUtils.ts</a>).
    /// The documentation's <c>~/.yarn/berry</c> is the location on other systems.
    /// </summary>
    public static string DefaultGlobalFolder(IUserEnvironment environment) =>
        Path.Combine(environment.LocalAppData, "Yarn", "Berry");

    /// <summary>
    /// The cache inside a global folder. It is Yarn 4's global cache, which <c>cacheFolder</c> is forced
    /// to while <c>enableGlobalCache</c> is on, and the mirror of Yarn 2 and 3, which projects copy
    /// from into their own <c>.yarn\cache</c>
    /// (<a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/yarnpkg-core/sources/Configuration.ts#L1416-L1419">Configuration.ts</a>,
    /// <a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/yarnpkg-core/sources/Cache.ts#L127-L133">Cache.ts</a>).
    /// </summary>
    public static string CacheIn(string globalFolder) => Path.Combine(globalFolder, "cache");

    /// <summary>
    /// Whether <paramref name="name"/> is a file Berry writes into its cache. Nothing else is written
    /// there: no folder, and no <c>.gitignore</c>, which Berry writes only into a project's own cache.
    ///
    /// <list type="bullet">
    /// <item>A package archive, <c>&lt;ident&gt;-&lt;protocol&gt;[-&lt;version&gt;]-&lt;10 hex&gt;-&lt;cache key&gt;.zip</c>.
    /// The key is the cache version and, from 3.x, <c>c</c> and a compression level: <c>10c0</c> by
    /// default in Yarn 4, <c>8</c> in Yarn 3 and <c>7</c> in Yarn 2. A cache beside a mirror names an
    /// archive by its checksum instead, ten more hexadecimal characters
    /// (<a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/yarnpkg-core/sources/Cache.ts#L135-L150">Cache.ts</a>,
    /// <a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/yarnpkg-core/sources/structUtils.ts#L690-L731">structUtils.ts</a>).</item>
    /// <item>An archive being written, <c>&lt;archive&gt;-&lt;16 hex&gt;.tmp</c>, renamed into place
    /// once whole (Yarn 3 and 4,
    /// <a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/yarnpkg-core/sources/Cache.ts#L379-L394">Cache.ts</a>).</item>
    /// <item>An archive's lock, <c>&lt;archive&gt;.flock</c> (Yarn 2,
    /// <a href="https://github.com/yarnpkg/berry/blob/599437dded9781b634aea272f9c308df157d6739/packages/yarnpkg-core/sources/Cache.ts#L222-L232">Cache.ts</a>).</item>
    /// </list>
    /// </summary>
    public static bool IsCacheFile(string name) => CacheFile().IsMatch(name);

    [GeneratedRegex(@"\A[^\\/]+-[0-9a-f]{10}-(?:\d+(?:c\d)?|[0-9a-f]{10})\.zip(?:-[0-9a-f]{16}\.tmp|\.flock)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CacheFile();
}
