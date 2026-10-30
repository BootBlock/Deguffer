using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One folder setting as <c>composer config --global --absolute</c> printed it.
/// </summary>
/// <param name="Reported">The line Composer printed, for a plan to quote.</param>
/// <param name="Folder">The full path it names, or null where it names none.</param>
/// <param name="Disabled">
/// Whether the value is one Composer reads as "no cache": <c>clear-cache</c> skips such a setting
/// rather than emptying anything (<c>Cache::isUsable</c>).
/// </param>
internal sealed record ComposerFolder(string Reported, string? Folder, bool Disabled);

/// <summary>
/// The global settings that decide what <c>composer clear-cache</c> empties and what sits beside it,
/// as Composer itself reports them.
///
/// <para><c>clear-cache</c> empties four settings' folders outright: <c>cache-vcs-dir</c>,
/// <c>cache-repo-dir</c>, <c>cache-files-dir</c> and then <c>cache-dir</c>, each through
/// <c>Cache::clear</c>, which deletes every entry in the folder and keeps the folder
/// (<a href="https://github.com/composer/composer/blob/2.11.1/src/Composer/Command/ClearCacheCommand.php">ClearCacheCommand.php</a>,
/// <a href="https://github.com/composer/composer/blob/2.4.4/src/Composer/Cache.php">Cache.php</a>).
/// Composer has no guard against a setting that names a folder holding something else, so every
/// one of them is asked rather than assumed to be its default of <c>{$cache-dir}/files</c>,
/// <c>/repo</c> and <c>/vcs</c>.</para>
///
/// <para><b>Asking writes, once.</b> Composer's <c>createConfig</c> creates its home and cache
/// folders, an <c>.htaccess</c> in each, and an empty <c>config.json</c> and <c>auth.json</c> where
/// they are missing, on any command. Every Composer that has ever run has written them already, so
/// on a machine that uses Composer the query changes nothing.</para>
/// </summary>
internal sealed partial record ComposerSettings(
    ComposerFolder Home,
    ComposerFolder DataDirectory,
    ComposerFolder Cache,
    ComposerFolder Files,
    ComposerFolder Repo,
    ComposerFolder Vcs,
    bool ReadOnly)
{
    /// <summary>
    /// The three settings <c>clear-cache</c> empties besides the cache folder itself, each with
    /// the child of it Composer defaults it to (<c>Config::$defaultConfig</c>).
    /// </summary>
    public IEnumerable<(string Setting, string DefaultChild, ComposerFolder Value)> CacheParts =>
    [
        ("cache-files-dir", "files", Files),
        ("cache-repo-dir", "repo", Repo),
        ("cache-vcs-dir", "vcs", Vcs),
    ];

    /// <summary>
    /// Ask Composer for every setting, or null where any answer is missing: a plan that knew only some
    /// of the folders <c>clear-cache</c> empties could not say what it removes.
    ///
    /// <para><c>--working-dir</c> names Deguffer's own empty folder, which is also where the runner
    /// starts every tool. Composer 2.11 loads a project's <c>composer.json</c> from there before its
    /// global configuration, and with <c>use-parent-dir</c> set it moves to the nearest parent holding
    /// one unless <c>--working-dir</c> was given (<c>Application::doRun</c>).</para>
    /// </summary>
    public static async Task<ComposerSettings?> AskAsync(
        IProcessRunner runner, string composer, string workingDirectory, CancellationToken ct)
    {
        var home = await AskFolderAsync(runner, composer, workingDirectory, "home", ct).ConfigureAwait(false);
        var data = await AskFolderAsync(runner, composer, workingDirectory, "data-dir", ct).ConfigureAwait(false);
        var cache = await AskFolderAsync(runner, composer, workingDirectory, "cache-dir", ct).ConfigureAwait(false);
        var files = await AskFolderAsync(runner, composer, workingDirectory, "cache-files-dir", ct).ConfigureAwait(false);
        var repo = await AskFolderAsync(runner, composer, workingDirectory, "cache-repo-dir", ct).ConfigureAwait(false);
        var vcs = await AskFolderAsync(runner, composer, workingDirectory, "cache-vcs-dir", ct).ConfigureAwait(false);
        var readOnly = await AskAsync(runner, composer, workingDirectory, "cache-read-only", ct).ConfigureAwait(false);

        if (home is null || data is null || cache is null || files is null || repo is null || vcs is null)
        {
            return null;
        }

        // Composer 2.4 prints a false setting as PHP casts it, an empty line, and a true one as "1";
        // later versions print both as JSON. Anything else is not an answer to read either way.
        bool? isReadOnly = readOnly switch
        {
            "" or "false" => false,
            "1" or "true" => true,
            _ => null,
        };

        return isReadOnly is { } value ? new ComposerSettings(home, data, cache, files, repo, vcs, value) : null;
    }

    /// <summary>The arguments that ask for one global setting from Deguffer's folder.</summary>
    public static string QueryArguments(string workingDirectory, string setting) =>
        $"config --global --absolute --no-interaction --working-dir \"{workingDirectory}\" {setting}";

    /// <summary>The arguments that clear the cache from Deguffer's folder, for the reason <see cref="AskAsync"/> gives.</summary>
    public static string ClearArguments(string workingDirectory) =>
        $"clear-cache --no-interaction --working-dir \"{workingDirectory}\"";

    private static async Task<ComposerFolder?> AskFolderAsync(
        IProcessRunner runner, string composer, string workingDirectory, string setting, CancellationToken ct)
    {
        if (await AskAsync(runner, composer, workingDirectory, setting, ct).ConfigureAwait(false) is not { Length: > 0 } reported)
        {
            return null;
        }

        return DisabledValue().IsMatch(reported)
            ? new ComposerFolder(reported, Folder: null, Disabled: true)
            : new ComposerFolder(reported, LongPath.Configured(reported), Disabled: false);
    }

    /// <summary>
    /// The last line Composer printed to stdout, or null where it failed. Its notes and warnings go to
    /// stderr, and the value alone to stdout (<c>ConfigCommand::execute</c>).
    /// </summary>
    private static async Task<string?> AskAsync(
        IProcessRunner runner, string composer, string workingDirectory, string setting, CancellationToken ct)
    {
        var outcome = await runner.RunAsync(composer, QueryArguments(workingDirectory, setting), ToolRun.Query, ct)
            .ConfigureAwait(false);

        return outcome.Succeeded
            ? outcome.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? string.Empty
            : null;
    }

    /// <summary>
    /// <c>Cache::isUsable</c>'s pattern, case-sensitive as Composer's is: a setting naming the null
    /// device turns that cache off.
    /// </summary>
    [GeneratedRegex(@"(^|[\\/])(\$null|nul|NUL|/dev/null)([\\/]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DisabledValue();
}
