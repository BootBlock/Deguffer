using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Everything the Arduino CLI reads when it starts to decide where its folders are, as it stood at one
/// moment: the configuration file it would open and the environment variables that move its folders.
///
/// <para><b>Why it is kept.</b> The CLI resolves its folders afresh each time it starts, so a setting
/// edited between the preview and the clean would send <c>cache clean</c> to a folder nobody examined,
/// and that command removes the folder it is sent to outright. The plan records this when it asks the
/// CLI, and the clean reads it again and compares (see <see cref="ArduinoCleanTarget"/>), which needs
/// no subprocess.</para>
///
/// <para><b>Which file.</b> <c>--config-file</c> where the row names one, as it does for Arduino IDE
/// 2's configuration; otherwise the first of <c>ARDUINO_CONFIG_FILE</c>,
/// <c>ARDUINO_DIRECTORIES_DATA</c> and <c>ARDUINO_DATA_DIR</c> that is set, the last two with
/// <c>arduino-cli.yaml</c> inside; otherwise <c>arduino-cli.yaml</c> in the default data folder
/// (<c>FindConfigFlagsInArgsOrFallbackOnEnv</c> in <c>internal/cli/configuration/configuration.go</c>).
/// The CLI binds every <c>ARDUINO_</c> variable to the setting it names, so the two that name the
/// downloads folder are kept as well.</para>
/// </summary>
internal sealed class ArduinoConfiguration
{
    /// <summary>Larger than any configuration the CLI writes, and bounded so a stray file is not read whole.</summary>
    private const int MaximumBytes = 1024 * 1024;

    /// <summary>The variables that decide which file is read, or where the data or downloads folder is.</summary>
    private static readonly string[] Variables =
    [
        "ARDUINO_CONFIG_FILE",
        "ARDUINO_DIRECTORIES_DATA",
        "ARDUINO_DATA_DIR",
        "ARDUINO_DIRECTORIES_DOWNLOADS",
        "ARDUINO_DOWNLOADS_DIR",
    ];

    private readonly string?[] _values;
    private readonly byte[]? _content;

    private ArduinoConfiguration(string?[] values, string? file, PathPresence presence, byte[]? content)
    {
        _values = values;
        File = file;
        Presence = presence;
        _content = content;
    }

    /// <summary>The file the CLI opens, or null where the setting naming it is not a full path.</summary>
    public string? File { get; }

    /// <summary>Whether the file was there, and could be read, when this was recorded.</summary>
    public PathPresence Presence { get; }

    /// <summary>
    /// Why the CLI's folders cannot be held to this record, as the end of a sentence, or null where
    /// they can.
    /// </summary>
    public string? WhyUnknown => (File, Presence) switch
    {
        (null, _) => "the setting that names its configuration file is not a full path, so Deguffer cannot tell which file it reads.",
        (_, PathPresence.Refused) => $"Windows would not let Deguffer read its configuration file {File}.",
        _ => null,
    };

    /// <summary>What the CLI would read now, started with <paramref name="configFile"/> as its <c>--config-file</c>.</summary>
    /// <param name="variable">
    /// Reads a variable, or null where it is unset, from the environment the CLI starts with: the one
    /// Deguffer hands it, or the one a CLI the user starts now has.
    /// </param>
    /// <param name="defaultData">The data folder where nothing moves it.</param>
    public static ArduinoConfiguration Read(Func<string, string?> variable, string? configFile, string defaultData)
    {
        var values = Variables.Select(variable).ToArray();

        // The CLI asks whether each variable is set, not whether it holds anything, so an empty one
        // still decides, and names no full path.
        var named = configFile
            ?? values[0]
            ?? ((values[1] ?? values[2]) is { } data ? Path.Combine(data, "arduino-cli.yaml") : null)
            ?? Path.Combine(defaultData, "arduino-cli.yaml");

        var file = LongPath.Configured(named);

        if (file is null)
        {
            return new ArduinoConfiguration(values, null, PathPresence.Refused, null);
        }

        return LongPath.ProbeFile(file) switch
        {
            PathPresence.Absent => new ArduinoConfiguration(values, file, PathPresence.Absent, null),
            PathPresence.Present when BoundedFile.ReadVerbatim(file, MaximumBytes) is { } bytes =>
                new ArduinoConfiguration(values, file, PathPresence.Present, bytes.ToArray()),
            _ => new ArduinoConfiguration(values, file, PathPresence.Refused, null),
        };
    }

    /// <summary>Whether <paramref name="other"/> would send the CLI to the same folders, as far as its inputs say.</summary>
    public bool SameAs(ArduinoConfiguration other) =>
        WhyUnknown is null
        && other.WhyUnknown is null
        && _values.SequenceEqual(other._values, StringComparer.Ordinal)
        && string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase)
        && Presence == other.Presence
        && (_content ?? []).AsSpan().SequenceEqual(other._content ?? []);
}
