using System.Text.Json;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the <c>yarn</c> on the PATH says about the global folder of Yarn 2 and later (Berry), from
/// Deguffer's own folder, and the facts about that folder that hold whichever Yarn is asked.
/// Every behaviour here is read from yarnpkg/berry at @yarnpkg/cli/4.18.1
/// (<c>75ebe063092f86e47e4afeaf1ae4bdf24402874b</c>), 3.8.7 and 2.4.3.
/// </summary>
/// <param name="Version">What <c>yarn -v</c> printed.</param>
/// <param name="IsBerry">Whether that is Yarn 2 or later, the only Yarn whose settings name the global folder.</param>
/// <param name="GlobalFolder">
/// The global folder Berry reports, as a full path, or null where it was not asked, said nothing, or
/// named something that is not a full path.
/// </param>
/// <param name="CleanDisabled">
/// Whether Berry's settings turn its cache clean off (<c>enableCacheClean: false</c>, from Yarn 4.6),
/// which says the cache is not to be cleared.
/// </param>
internal sealed partial record YarnBerryCache(string Version, bool IsBerry, string? GlobalFolder, bool CleanDisabled)
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
    /// Ask <paramref name="yarn"/> which Yarn it is and, for Berry, where its global folder is and
    /// whether its cache clean is turned off. Null where it would not say which Yarn it is.
    ///
    /// <para><c>config get</c> works outside a project in every Berry release and prints no banner.
    /// With <c>--json</c> it prints one JSON value, a path in Windows form, and no colour
    /// (<a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/plugin-essentials/sources/commands/config/get.ts#L52-L98">get.ts</a>).
    /// It reads <c>YARN_GLOBAL_FOLDER</c> and the <c>.yarnrc.yml</c> in the profile, which a project's
    /// own settings can override for that project alone. A Yarn before 4.6 does not know
    /// <c>enableCacheClean</c>, fails the question, and has no such setting to honour.</para>
    /// </summary>
    public static async Task<YarnBerryCache?> AskAsync(IProcessRunner runner, string yarn, CancellationToken ct)
    {
        if ((await runner.RunAsync(yarn, "-v", ToolRun.Query, ct).ConfigureAwait(false)).LastLine is not { } version)
        {
            return null;
        }

        if (!BerryVersion().IsMatch(version))
        {
            return new YarnBerryCache(version, IsBerry: false, GlobalFolder: null, CleanDisabled: false);
        }

        var global = Value(await runner.RunAsync(yarn, "config get globalFolder --json", ToolRun.Query, ct).ConfigureAwait(false));
        var clean = Value(await runner.RunAsync(yarn, "config get enableCacheClean --json", ToolRun.Query, ct).ConfigureAwait(false));

        return new YarnBerryCache(
            version,
            IsBerry: true,
            global is { ValueKind: JsonValueKind.String } path ? LongPath.Configured(path.GetString()) : null,
            CleanDisabled: clean is { ValueKind: JsonValueKind.False });
    }

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

    private static JsonElement? Value(CommandOutcome outcome)
    {
        if (outcome.LastLine is not { } line)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not an answer: --json prints one JSON value, and anything else is not it.
            return null;
        }
    }

    /// <summary>A release of Yarn 2 or later, as <c>yarn -v</c> prints it, release candidates included.</summary>
    [GeneratedRegex(@"^(?:[2-9]|[1-9]\d+)\.\d+\.\d+(?:-[0-9A-Za-z.]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BerryVersion();

    [GeneratedRegex(@"\A[^\\/]+-[0-9a-f]{10}-(?:\d+(?:c\d)?|[0-9a-f]{10})\.zip(?:-[0-9a-f]{16}\.tmp|\.flock)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CacheFile();
}
