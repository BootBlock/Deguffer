using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the <c>yarn</c> on the PATH says about itself and its cache, from Deguffer's own folder.
/// </summary>
/// <param name="Version">What <c>yarn -v</c> printed.</param>
/// <param name="IsClassic">Whether that is Yarn 1, the only Yarn whose cache this clears.</param>
/// <param name="Reported">What <c>yarn cache dir</c> printed, or null where it was not asked or said nothing.</param>
/// <param name="Root">
/// The cache root <c>yarn cache clean</c> deletes: the folder holding the versioned one
/// <c>cache dir</c> reports. Null where the answer is not a full path ending in a version folder.
/// </param>
internal sealed partial record YarnClassicCache(string Version, bool IsClassic, string? Reported, string? Root)
{
    /// <summary>
    /// Ask <paramref name="yarn"/> which Yarn it is, and where a Yarn 1 keeps its cache. Null where
    /// it would not say which Yarn it is.
    ///
    /// <para><b>The version is asked, never inferred from the program's location.</b> Yarn 1 hands
    /// itself off to whatever a <c>yarn-path</c> or <c>yarnPath</c> setting names before it reads any
    /// command, <c>-v</c> included, and it reads those settings from <c>.yarnrc</c> and
    /// <c>.yarnrc.yml</c> in the profile as well as in every parent of the working folder
    /// (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/cli/index.js#L631-L662">index.js</a>,
    /// <a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/util/rc.js">rc.js</a>). A Corepack
    /// shim runs whichever Yarn its last known good version names. So a classic install can run
    /// Yarn 4, and only asking the program from the folder the clean runs in tells which.</para>
    ///
    /// <para><c>yarn -v</c> as the first argument prints the version alone and exits, in Yarn 1
    /// (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/cli/index.js#L164-L168">index.js</a>)
    /// and in Berry (<a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/plugin-essentials/sources/commands/entries/version.ts">version.ts</a>).
    /// <c>yarn cache dir</c> prints the cache folder in use, the versioned <c>v6</c> one, with no
    /// banner (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/cli/commands/cache.js">cache.js</a>);
    /// warnings go to stderr.</para>
    /// </summary>
    public static async Task<YarnClassicCache?> AskAsync(IProcessRunner runner, string yarn, CancellationToken ct)
    {
        if (LastLine(await runner.RunAsync(yarn, "-v", ToolRun.Query, ct).ConfigureAwait(false)) is not { } version)
        {
            return null;
        }

        if (!ClassicVersion().IsMatch(version))
        {
            return new YarnClassicCache(version, IsClassic: false, Reported: null, Root: null);
        }

        var reported = LastLine(await runner.RunAsync(yarn, "cache dir", ToolRun.Query, ct).ConfigureAwait(false));

        return new YarnClassicCache(version, IsClassic: true, reported, RootOf(reported));
    }

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

    private static string? RootOf(string? reported) =>
        LongPath.Configured(reported) is { } folder
        && IsVersionFolder(Path.GetFileName(folder))
        && Path.GetDirectoryName(folder) is { } root
        && Path.GetDirectoryName(root) is not null
            ? root
            : null;

    private static string? LastLine(CommandOutcome outcome) =>
        outcome.Succeeded
            ? outcome.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault()
            : null;

    [GeneratedRegex(@"^1\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ClassicVersion();

    [GeneratedRegex(@"^v\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFolder();
}
