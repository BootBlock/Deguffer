using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <c>npm config get cache userconfig globalconfig prefix</c> said: where npm keeps its cache,
/// and the three paths that decide where npm reads its configuration from.
///
/// <para><b>Why the three are asked with the cache.</b> The cache is configuration, and npm reads it
/// from files only npm can name: the user configuration (<c>userconfig</c>), the global configuration
/// (<c>globalconfig</c>, which follows <c>prefix</c>), and the npm installed under <c>prefix</c>, which
/// the shim runs in preference to the one bundled beside it. The answer is remembered for as long as
/// those files read the same (<see cref="ToolAnswerMemory"/>), so they are asked for in the same run
/// rather than guessed.</para>
/// </summary>
/// <param name="Cache">The cache directory npm reported, or the documented default where it did not.</param>
/// <param name="UserConfig">The user configuration file npm reads, or null where it named none as a full path.</param>
/// <param name="GlobalConfig">The global configuration file npm reads, or null where it named none as a full path.</param>
/// <param name="Prefix">npm's global prefix, or null where it named none as a full path.</param>
internal sealed record NpmLocations(AskedLocation Cache, string? UserConfig, string? GlobalConfig, string? Prefix)
{
    /// <summary>The configuration keys asked for, in the order npm is asked.</summary>
    public const string Keys = "cache userconfig globalconfig prefix";

    /// <summary>
    /// Whether npm answered every key, so everything the answer depends on is known. Anything less is
    /// planned from, but never remembered, because a configuration file it could not name is a change
    /// nobody would notice.
    /// </summary>
    public bool IsComplete => Cache.Answered && UserConfig is not null && GlobalConfig is not null && Prefix is not null;

    /// <summary>
    /// The paths npm printed, one <c>key=value</c> line per key as npm prints several, keys compared
    /// without case and only full paths kept.
    ///
    /// <para>An output with no <c>cache=</c> line is read as npm prints a single key, by its last
    /// rooted line, and a failed query names nothing, whatever it printed
    /// (<see cref="AskedLocation.FromLastRootedLine"/>).</para>
    /// </summary>
    /// <param name="outcome">The query's outcome.</param>
    /// <param name="defaultCache">The documented default cache directory.</param>
    public static NpmLocations From(CommandOutcome outcome, string defaultCache)
    {
        string? cache = null, userConfig = null, globalConfig = null, prefix = null;

        if (outcome.Succeeded)
        {
            foreach (var line in outcome.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var equals = line.IndexOf('=', StringComparison.Ordinal);

                if (equals <= 0)
                {
                    continue;
                }

                var key = line.AsSpan(0, equals).Trim();
                var value = line[(equals + 1)..].Trim();

                if (!Path.IsPathRooted(value))
                {
                    continue;
                }

                if (key.Equals("cache", StringComparison.OrdinalIgnoreCase))
                {
                    cache = value;
                }
                else if (key.Equals("userconfig", StringComparison.OrdinalIgnoreCase))
                {
                    userConfig = value;
                }
                else if (key.Equals("globalconfig", StringComparison.OrdinalIgnoreCase))
                {
                    globalConfig = value;
                }
                else if (key.Equals("prefix", StringComparison.OrdinalIgnoreCase))
                {
                    prefix = value;
                }
            }
        }

        var location = cache is null ? AskedLocation.FromLastRootedLine(outcome, defaultCache) : AskedLocation.Of(cache, defaultCache);

        return new NpmLocations(location, userConfig, globalConfig, prefix);
    }
}
