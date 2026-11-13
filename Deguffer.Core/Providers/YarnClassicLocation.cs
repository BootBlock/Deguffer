using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Where Yarn 1 said it keeps its cache.</summary>
/// <param name="Reported">What <c>yarn cache dir</c> printed, or null where it failed or said nothing.</param>
internal sealed record YarnClassicLocation(string? Reported)
{
    /// <summary>
    /// The cache root <c>yarn cache clean</c> deletes: the folder holding the versioned one
    /// <c>cache dir</c> reports. Null where the answer is not a full path ending in a version folder.
    /// </summary>
    public string? Root =>
        LongPath.Configured(Reported) is { } folder
        && YarnClassicCache.IsVersionFolder(Path.GetFileName(folder))
        && Path.GetDirectoryName(folder) is { } root
        && Path.GetDirectoryName(root) is not null
            ? root
            : null;

    /// <summary>
    /// Ask <paramref name="yarn"/>, a Yarn 1, where its cache is. <c>yarn cache dir</c> prints the cache
    /// folder in use, the versioned <c>v6</c> one, with no banner
    /// (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/cli/commands/cache.js">cache.js</a>);
    /// warnings go to stderr. The script its <c>yarn-path</c> names was asked with its version
    /// (<see cref="YarnVersionAnswer"/>).
    /// </summary>
    public static async Task<YarnClassicLocation> AskAsync(IProcessRunner runner, string yarn, CancellationToken ct) =>
        new((await runner.RunAsync(yarn, "cache dir", ToolRun.Query, ct).ConfigureAwait(false)).LastLine);
}
