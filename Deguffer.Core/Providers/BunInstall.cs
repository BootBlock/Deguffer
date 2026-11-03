using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the <c>bun</c> on the PATH says about itself and its install cache, from Deguffer's own folder.
/// </summary>
/// <param name="Version">What <c>bun --version</c> printed, where it parsed as a version.</param>
/// <param name="Reported">What <c>bun pm cache</c> printed, or null where it was not asked or said nothing.</param>
/// <param name="Cache">The full path it names, or null where it names none.</param>
internal sealed partial record BunInstall(Version Version, string? Reported, string? Cache)
{
    /// <summary>
    /// The first Bun with <c>bun pm cache prune</c>
    /// (<a href="https://bun.com/blog/bun-v1.4.4">Bun v1.4.4</a>, commit <c>9510e7415207</c>). Every
    /// earlier Bun's only eviction is <c>bun pm cache rm</c>, which deletes the <c>links</c> store with
    /// everything else, and none of 1.2.23 to 1.4.3 has <c>prune</c> in its dispatch.
    /// </summary>
    public static readonly Version FirstWithPrune = new(1, 4, 4);

    /// <summary>Whether this Bun has <c>prune</c>. An earlier one reads the word as a request for the cache's path.</summary>
    public bool CanPrune => Version >= FirstWithPrune;

    /// <summary>
    /// Ask <paramref name="bun"/> its version, and where a Bun that can prune keeps its cache. Null
    /// where it would not say its version.
    ///
    /// <para>The cache is asked only of a Bun that can prune, because until 1.4.4 every <c>bun pm</c>
    /// command, the bare <c>bun pm cache</c> included, refuses to run without a <c>package.json</c> in
    /// the working folder or above it
    /// (<a href="https://github.com/oven-sh/bun/blob/bun-v1.4.3/src/runtime/cli/package_manager_command.rs#L267-L281">1.4.3</a>),
    /// and Deguffer's folder has none. 1.4.4 lets <c>cache</c> run without one, and prints the path
    /// with no line ending
    /// (<a href="https://github.com/oven-sh/bun/blob/bun-v1.4.4/src/runtime/cli/package_manager_command.rs">package_manager_command.rs</a>).</para>
    /// </summary>
    public static async Task<BunInstall?> AskAsync(IProcessRunner runner, string bun, CancellationToken ct)
    {
        if ((await runner.RunAsync(bun, "--version", ToolRun.Query, ct).ConfigureAwait(false)).LastLine is not { } printed
            || PlainVersion().Match(printed) is not { Success: true } match)
        {
            return null;
        }

        var version = new Version(
            int.Parse(match.Groups[1].ValueSpan, provider: null),
            int.Parse(match.Groups[2].ValueSpan, provider: null),
            int.Parse(match.Groups[3].ValueSpan, provider: null));

        if (version < FirstWithPrune)
        {
            return new BunInstall(version, Reported: null, Cache: null);
        }

        var reported = (await runner.RunAsync(bun, "pm cache", ToolRun.Query, ct).ConfigureAwait(false)).LastLine;

        return new BunInstall(version, reported, LongPath.Configured(reported) is { } cache && Path.GetDirectoryName(cache) is not null ? cache : null);
    }

    /// <summary>
    /// The prune, sent to <paramref name="cache"/> by name so it acts on the folder this plan measured,
    /// whatever <c>BUN_INSTALL_CACHE_DIR</c> or <c>~/.bunfig.toml</c> say by the time it runs
    /// (<c>cli.cache_dir</c> wins over both). <c>--max-age 0</c> makes every package folder old
    /// enough; the default keeps those used in the last 30 days. Never <c>rm</c>, which deletes the
    /// <c>links</c> store as well.
    /// </summary>
    public static string PruneArguments(string cache) => $"pm cache prune --max-age 0 --cache-dir \"{cache}\"";

    /// <summary><c>bun --version</c> prints the version alone, and a canary build prints no suffix.</summary>
    [GeneratedRegex(@"^(\d{1,9})\.(\d{1,9})\.(\d{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainVersion();
}
