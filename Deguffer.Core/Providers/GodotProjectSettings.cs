using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a Godot project's <c>project.godot</c> says about where the editor keeps its project data,
/// read from the file rather than guessed from a folder's name.
///
/// <para><b>The name comes from two settings.</b> <c>config_version</c>, written at the top of the
/// file, says which engine wrote it: 5 is Godot 4, and 3 or 4 is Godot 3. The engine names the data
/// folder <c>godot</c> in Godot 4 and <c>import</c> in Godot 3, with a dot in front unless
/// <c>application/config/use_hidden_project_data_directory</c> is false. That setting defaults to true
/// in both (<see href="https://github.com/godotengine/godot/blob/master/core/config/project_settings.cpp">project_settings.cpp</see>,
/// and the same file on the <c>3.x</c> branch).</para>
///
/// <para><b>Anything in the project that could change the answer declines it.</b> The engine reads a
/// <c>project.binary</c> in preference to <c>project.godot</c>, so one present declines the project.
/// It then applies an <c>override.cfg</c> beside it and the file
/// <c>application/config/project_settings_override</c> names, and a setting can carry a feature-tag
/// variant such as <c>.windows</c> that applies on some machines only. A variant of the setting
/// declines the project, and so does an override file in the project that names the setting or
/// cannot be read.</para>
///
/// <para><b>Two overrides cannot be seen from the project</b>: an <c>override.cfg</c> beside the
/// editor's executable, and a settings-override file outside the project, such as in
/// <c>user://</c>, whose folder depends on settings and on the machine. Projects use
/// such a file for per-user settings, and a project that names one is not declined for it, or a
/// developer's main project would never be offered. In those cases the name rests on
/// <c>project.godot</c> alone. That is safe because the name only chooses which folder is looked in:
/// a cache inside it is offered only where every entry has the shape the engine writes
/// (<see cref="GodotCacheContents"/>).</para>
/// </summary>
/// <param name="ConfigVersion">The file's <c>config_version</c>.</param>
/// <param name="DataFolderName">The name of the folder the editor keeps its project data in.</param>
public sealed record GodotProjectSettings(int ConfigVersion, string DataFolderName)
{
    /// <summary>The file every Godot project has at its top, and the editor opens it by.</summary>
    public const string ProjectFile = "project.godot";

    /// <summary>The first <c>config_version</c> Godot 4 writes. Godot 3 wrote 3, then 4.</summary>
    public const int FirstGodot4ConfigVersion = 5;

    private const string HiddenSetting = "config/use_hidden_project_data_directory";
    private const string OverrideFileSetting = "config/project_settings_override";
    private const string ApplicationSection = "application";

    /// <summary>
    /// Far beyond what the editor writes. A project's input map and autoloads make the file longest,
    /// and the longest measured was under 40 KB. See <see cref="BoundedFile"/> for why a larger file
    /// is not read.
    /// </summary>
    private const int MaximumBytes = 1024 * 1024;

    /// <summary>Whether the project is a Godot 4 project, whose data folder holds the caches as children.</summary>
    public bool IsGodot4 => ConfigVersion >= FirstGodot4ConfigVersion;

    /// <summary>
    /// The settings of the project in <paramref name="project"/>, or null where they cannot be known:
    /// no readable <c>project.godot</c>, a file the reader does not understand, or one of the
    /// overrides the summary names.
    /// </summary>
    public static GodotProjectSettings? Read(string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        // A binary settings file wins over the text one in the engine, and an override file could
        // set the name. A refusal is not absence (§5.2), so only a definite "not there" passes.
        if (LongPath.ProbeEntry(Path.Combine(project, "project.binary")) is not PathPresence.Absent
            || OverrideNamesTheSetting(Path.Combine(project, "override.cfg")) is not false)
        {
            return null;
        }

        if (Parse(Path.Combine(project, ProjectFile)) is not { } values
            || values.ConfigVersion is not { } version
            || values.HasVariant
            || values.OverrideFiles.Any(file => InProject(project, file) is { } path && OverrideNamesTheSetting(path) is not false))
        {
            return null;
        }

        var hidden = values.Hidden ?? true;
        var suffix = version >= FirstGodot4ConfigVersion ? "godot" : "import";

        return new GodotProjectSettings(version, (hidden ? "." : string.Empty) + suffix);
    }

    /// <summary>
    /// Where the settings-override file <paramref name="value"/> names is, where it is in the project,
    /// or null where it is not: <c>user://</c>, an absolute path or anything else the project cannot
    /// say. <paramref name="value"/> is the setting's value as written, quoted.
    /// </summary>
    private static string? InProject(string project, string value)
    {
        const string Resources = "\"res://";

        return value.StartsWith(Resources, StringComparison.Ordinal) && value.Length > Resources.Length + 1 && value[^1] == '"'
            ? Path.Combine(project, value[Resources.Length..^1].Replace('/', Path.DirectorySeparatorChar))
            : null;
    }

    /// <summary>
    /// Whether an override file at <paramref name="path"/> names the data-folder setting: false where
    /// there is no such file or it does not name it, true where it does, and null where Windows would
    /// not say or the file could not be read.
    /// </summary>
    private static bool? OverrideNamesTheSetting(string path) => LongPath.ProbeFile(path) switch
    {
        PathPresence.Absent => false,
        PathPresence.Present when Parse(path) is { } values => values.Hidden is not null || values.HasVariant,
        _ => null,
    };

    /// <summary>The values this reader looks for in one file of Godot's settings format.</summary>
    private sealed record Values(int? ConfigVersion, bool? Hidden, bool HasVariant, IReadOnlyList<string> OverrideFiles);

    /// <summary>
    /// The values in the file at <paramref name="path"/>, or null where it is missing, too large, not
    /// UTF-8, or not in the format the engine writes.
    ///
    /// <para>The format is the engine's <c>ConfigFile</c> text: <c>[section]</c> headers, then
    /// <c>key=value</c> lines, with <c>;</c> comments. A value can run over several lines, as a
    /// dictionary or an array does, and a string can hold a line break. So a line is read as a key or
    /// a header only where no string or bracket is still open from the lines before it. Otherwise a
    /// project description holding the text <c>[application]</c> would move the reader into a section
    /// it is not in.</para>
    /// </summary>
    private static Values? Parse(string path)
    {
        if (BoundedFile.Read(path, MaximumBytes) is not { } content)
        {
            return null;
        }

        string text;

        try
        {
            text = BoundedFile.StrictUtf8.GetString(content.Span);
        }
        catch (DecoderFallbackException)
        {
            // The engine writes the file as UTF-8, so a file that is not is not one it wrote.
            return null;
        }

        int? version = null;
        bool? hidden = null;
        var variant = false;
        var overrideFiles = new List<string>();

        var section = string.Empty;
        var open = new ValueScanner();

        foreach (var line in text.Split('\n'))
        {
            if (open.IsInside)
            {
                open.Scan(line);
                continue;
            }

            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] == ';')
            {
                continue;
            }

            if (trimmed[0] == '[')
            {
                if (trimmed[^1] != ']')
                {
                    return null;
                }

                section = trimmed[1..^1].Trim();
                continue;
            }

            var equals = trimmed.IndexOf('=');

            if (equals <= 0)
            {
                return null;
            }

            var key = trimmed[..equals].Trim();
            var value = trimmed[(equals + 1)..].Trim();

            open.Scan(value);

            if (section.Length == 0 && key == "config_version")
            {
                if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    return null;
                }

                version = number;
            }
            else if (section == ApplicationSection && key == HiddenSetting)
            {
                hidden = value switch
                {
                    "true" => true,
                    "false" => false,
                    _ => null,
                };

                if (hidden is null)
                {
                    return null;
                }
            }
            else if (section == ApplicationSection && key.StartsWith(HiddenSetting + ".", StringComparison.Ordinal))
            {
                variant = true;
            }
            else if (section == ApplicationSection && key.StartsWith(OverrideFileSetting, StringComparison.Ordinal))
            {
                // Every variant is kept: which one the engine applies depends on the machine. An empty
                // string names no file.
                if (value is not "\"\"")
                {
                    overrideFiles.Add(value);
                }
            }
        }

        // A string or a bracket still open at the end, or one closed that never opened, is a file the
        // engine would not have written.
        return open.IsInside || open.IsBroken ? null : new Values(version, hidden, variant, overrideFiles);
    }

    /// <summary>
    /// Whether the text read so far leaves a string or a bracket open, so the next line continues a
    /// value rather than starting a key.
    /// </summary>
    private sealed class ValueScanner
    {
        private bool _inString;
        private bool _escaped;
        private int _depth;

        public bool IsInside => _inString || _depth > 0;

        public bool IsBroken { get; private set; }

        public void Scan(string text)
        {
            foreach (var c in text)
            {
                if (_inString)
                {
                    if (_escaped)
                    {
                        _escaped = false;
                    }
                    else if (c == '\\')
                    {
                        _escaped = true;
                    }
                    else if (c == '"')
                    {
                        _inString = false;
                    }
                }
                else if (c == '"')
                {
                    _inString = true;
                }
                else if (c is '[' or '{' or '(')
                {
                    _depth++;
                }
                else if (c is ']' or '}' or ')')
                {
                    IsBroken |= --_depth < 0;
                }
            }
        }
    }
}
