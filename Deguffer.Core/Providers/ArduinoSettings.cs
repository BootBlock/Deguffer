using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One folder setting as <c>arduino-cli config get</c> printed it.</summary>
/// <param name="Reported">The value the CLI printed, for a plan to quote.</param>
/// <param name="Folder">The full path it names, or null where it names none.</param>
internal sealed record ArduinoFolder(string Reported, string? Folder);

/// <summary>
/// The settings of one Arduino CLI configuration that decide what <c>arduino-cli cache clean</c>
/// removes and what sits beside it, as the CLI itself reports them.
///
/// <para><b>Asked, not read from the file.</b> The CLI resolves each setting from its configuration
/// file, then from <c>ARDUINO_</c> environment variables, then from its defaults
/// (<c>internal/cli/configuration</c>). The defaults depend on the Windows known folders, and an
/// unset <c>directories.downloads</c> means <c>{directories.data}\staging</c>
/// (<c>Settings.DownloadsDir</c>). <c>config get</c> prints an unset one as an empty string. One a
/// file sets to an empty string prints the same, and <c>cache clean</c> then names no folder and
/// removes nothing, so reading it as <c>staging</c> can only offer a clean that fails. Asking the CLI gets every one of those rules from the program that
/// applies them.</para>
///
/// <para><b>Two configurations.</b> The CLI on its own finds its file from <c>ARDUINO_CONFIG_FILE</c>,
/// then in its data folder. Arduino IDE 2 starts its bundled CLI with <c>--config-file</c> naming
/// <c>%USERPROFILE%\.arduinoIDE\arduino-cli.yaml</c> (<c>arduino-daemon-impl.ts</c>), so the IDE and
/// the CLI can keep their downloads in different places. A query names the same file the clean will.</para>
/// </summary>
/// <param name="ConfigFile">The file the IDE names with <c>--config-file</c>, or null for the CLI's own.</param>
/// <param name="Data">
/// <c>directories.data</c>: installed boards and tools in <c>packages</c>, the board and library
/// indexes, and <c>inventory.yaml</c>. Never removed.
/// </param>
/// <param name="Downloads">
/// Where downloaded archives are staged: <c>directories.downloads</c>, or <c>staging</c> in the data
/// folder where that is empty. <c>cache clean</c> removes this folder outright.
/// </param>
/// <param name="BuildCache"><c>build_cache.path</c>: compiled cores and sketches.</param>
/// <param name="Sketchbook"><c>directories.user</c>: the user's sketches and libraries. Never removed.</param>
internal sealed record ArduinoSettings(
    string? ConfigFile,
    ArduinoFolder Data,
    ArduinoFolder Downloads,
    ArduinoFolder BuildCache,
    ArduinoFolder Sketchbook)
{
    /// <summary>The folder inside the data folder that downloads go to where nothing moves them.</summary>
    public const string DefaultDownloadsName = "staging";

    /// <summary>
    /// Ask the CLI for every setting, or null where any answer is missing: a plan that knew only some
    /// of the folders could not say what <c>cache clean</c> removes, or what must survive it.
    /// </summary>
    public static async Task<ArduinoSettings?> AskAsync(
        IProcessRunner runner, string cli, string? configFile, CancellationToken ct)
    {
        var data = await AskAsync(runner, cli, configFile, "directories.data", ct).ConfigureAwait(false);
        var downloads = await AskAsync(runner, cli, configFile, "directories.downloads", ct).ConfigureAwait(false);
        var buildCache = await AskAsync(runner, cli, configFile, "build_cache.path", ct).ConfigureAwait(false);
        var sketchbook = await AskAsync(runner, cli, configFile, "directories.user", ct).ConfigureAwait(false);

        if (data is not { Length: > 0 } || downloads is null || buildCache is not { Length: > 0 } || sketchbook is not { Length: > 0 })
        {
            return null;
        }

        var dataFolder = Folder(data);

        // Settings.DownloadsDir: an unset setting is the data folder's staging, and a data folder that
        // is not a full path leaves the downloads unknown as well.
        var downloadsFolder = downloads.Length > 0
            ? Folder(downloads)
            : new ArduinoFolder(
                downloads,
                dataFolder.Folder is { } dataPath ? Path.Combine(dataPath, DefaultDownloadsName) : null);

        return new ArduinoSettings(configFile, dataFolder, downloadsFolder, Folder(buildCache), Folder(sketchbook));
    }

    /// <summary>The arguments that ask for one setting, from the configuration a clean would use.</summary>
    public static string QueryArguments(string? configFile, string setting) =>
        $"config get {setting} --json{ConfigArgument(configFile)}";

    /// <summary>The arguments that remove the downloads folder, from the same configuration.</summary>
    public static string CleanArguments(string? configFile) => $"cache clean{ConfigArgument(configFile)}";

    private static string ConfigArgument(string? configFile) =>
        configFile is null ? string.Empty : $" --config-file \"{configFile}\"";

    private static ArduinoFolder Folder(string reported) => new(reported, LongPath.Configured(reported));

    /// <summary>
    /// The string the CLI printed as JSON for <paramref name="setting"/>, or null where it failed or
    /// printed anything but one string. <c>config get</c> prints the value alone with <c>--json</c>
    /// (<c>internal/cli/config/get.go</c>).
    /// </summary>
    private static async Task<string?> AskAsync(
        IProcessRunner runner, string cli, string? configFile, string setting, CancellationToken ct)
    {
        var outcome = await runner.RunAsync(cli, QueryArguments(configFile, setting), ToolRun.Query, ct)
            .ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(outcome.StandardOutput);

            return document.RootElement.ValueKind is JsonValueKind.String ? document.RootElement.GetString() : null;
        }
        catch (JsonException)
        {
            // Not an answer to read: the value is one JSON string, and anything else is not it.
            return null;
        }
    }
}
