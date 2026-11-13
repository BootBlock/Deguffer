using System.Text.RegularExpressions;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the <c>yarn</c> on the PATH says about itself and, where it is Yarn 1, its cache, from
/// Deguffer's own folder.
/// </summary>
/// <param name="Version">Which Yarn it runs (<see cref="YarnVersion"/>).</param>
/// <param name="Location">Where Yarn 1 keeps its cache, or null where it is not Yarn 1 and was not asked.</param>
internal sealed partial record YarnClassicCache(YarnVersion Version, YarnClassicLocation? Location)
{
    /// <summary>
    /// Whether <paramref name="name"/> is one of Yarn 1's versioned cache folders. Yarn 1.22 writes
    /// <c>v6</c> (<c>CACHE_VERSION</c> in
    /// <a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/constants.js">constants.js</a>), and
    /// an earlier Yarn 1 wrote a lower number into the same root, which <c>yarn cache clean</c> removes
    /// with the rest.
    /// </summary>
    public static bool IsVersionFolder(string name) => VersionFolder().IsMatch(name);

    /// <summary>
    /// The clean, sent to <paramref name="root"/> by name. Two things follow from naming it. Yarn 1
    /// deletes the root this plan measured and vetted, whatever its settings say by the time it runs,
    /// because an explicit <c>--cache-folder</c> wins over them and has no fallback
    /// (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/config.js#L352-L372">config.js</a>).
    /// And Yarn 4 refuses the whole command line before running anything, because neither
    /// <c>--cache-folder</c> nor <c>--non-interactive</c> is an option of its own and clipanion rejects
    /// an unknown option while parsing, should a setting hand the program to it between the plan and
    /// the clean. Yarn 2 and 3 parse with older clipanion releases, which were not read, so for them
    /// the version asked at plan time is the guard.
    /// </summary>
    public static string CleanArguments(string root) => $"cache clean --cache-folder \"{root}\" --non-interactive";

    [GeneratedRegex(@"^v\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFolder();
}
