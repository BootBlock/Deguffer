using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Where Yarn 1 said it keeps its cache, and the script its <c>yarn-path</c> hands commands to.</summary>
/// <param name="Reported">What <c>yarn cache dir</c> printed, or null where it failed or said nothing.</param>
/// <param name="Script">What <c>yarn config get yarn-path</c> said.</param>
internal sealed record YarnClassicLocation(string? Reported, YarnScriptSetting Script)
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
    /// Whether Yarn answered both questions, so the answer and everything it depends on are known.
    /// Anything less is planned from and never remembered.
    /// </summary>
    public bool IsAnswer => Reported is not null && Script.Answered;

    /// <summary>
    /// Ask <paramref name="yarn"/>, a Yarn 1, both questions at once. <c>yarn cache dir</c> prints the
    /// cache folder in use, the versioned <c>v6</c> one, with no banner
    /// (<a href="https://github.com/yarnpkg/yarn/blob/v1.22.22/src/cli/commands/cache.js">cache.js</a>);
    /// warnings go to stderr.
    /// </summary>
    public static async Task<YarnClassicLocation> AskAsync(IProcessRunner runner, string yarn, CancellationToken ct)
    {
        var cache = runner.RunAsync(yarn, "cache dir", ToolRun.Query, ct);
        var script = runner.RunAsync(yarn, "config get yarn-path", ToolRun.Query, ct);

        // Both are awaited together, so a fault in one never leaves the other unobserved.
        await Task.WhenAll(cache, script).ConfigureAwait(false);

        return new YarnClassicLocation(
            (await cache.ConfigureAwait(false)).LastLine,
            YarnScriptSetting.FromClassic(await script.ConfigureAwait(false)));
    }
}
