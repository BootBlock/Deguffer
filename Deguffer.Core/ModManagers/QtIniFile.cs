using System.Globalization;
using System.Text;
using Deguffer.Core.Providers;

namespace Deguffer.Core.ModManagers;

/// <summary>
/// A settings file Qt's <c>QSettings</c> wrote in its INI form, read once into its sections and values.
///
/// <para>Mod Organizer 2 writes its instance settings, its download handler record and each download's
/// <c>.meta</c> through <c>QSettings</c>, so a value is in Qt's escaped form rather than as typed: a
/// backslash is written as <c>\\</c>, a value holding a comma, a semicolon, an equals sign or an outer
/// space is wrapped in quotes, and a control character, or under Qt 5 any character past ASCII, is
/// written as <c>\x</c> and its code. A path read without undoing that names a folder with doubled
/// separators, or none at all.</para>
///
/// <para>Keys and sections are compared without regard to case, as <c>QSettings</c> compares them on
/// Windows. A key written inside an array keeps the separator Qt gives it, as in <c>1\executable</c>.</para>
/// </summary>
internal sealed class QtIniFile
{
    /// <summary>The section <c>QSettings</c> writes a key with no group into.</summary>
    public const string General = "General";

    private readonly Dictionary<string, Dictionary<string, string>> _sections;

    private QtIniFile(Dictionary<string, Dictionary<string, string>> sections) => _sections = sections;

    /// <summary>
    /// The file at <paramref name="path"/>, or null where it is missing, larger than
    /// <paramref name="maximumBytes"/>, unreadable, or not UTF-8 text.
    /// </summary>
    public static QtIniFile? Read(string path, int maximumBytes)
    {
        if (BoundedFile.Read(path, maximumBytes) is not { } content)
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
            // Qt writes UTF-8 under Qt 6 and escapes everything past ASCII under Qt 5, so a file that is
            // neither is not one Mod Organizer 2 wrote.
            return null;
        }

        return Parse(text);
    }

    /// <summary>The file whose text is <paramref name="text"/>.</summary>
    public static QtIniFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // A key above the first section is a key with no group, which QSettings reads as General's.
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [General] = current,
        };

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();

                if (!sections.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[name] = current;
                }

                continue;
            }

            if (line.IndexOf('=') is > 0 and var equals)
            {
                // The first value wins, as it does where QSettings reads a key written twice.
                current.TryAdd(line[..equals].Trim(), Unescaped(line[(equals + 1)..]));
            }
        }

        return new QtIniFile(sections);
    }

    /// <summary>The value of <paramref name="key"/> in <paramref name="section"/>, or null where either is missing.</summary>
    public string? Value(string section, string key) =>
        _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : null;

    /// <summary>Whether <paramref name="key"/> in <paramref name="section"/> is <c>true</c>, as Qt writes a boolean.</summary>
    public bool IsTrue(string section, string key) =>
        string.Equals(Value(section, key), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every key and value in <paramref name="section"/>, empty where it is missing.</summary>
    public IReadOnlyDictionary<string, string> Section(string section) =>
        _sections.TryGetValue(section, out var values) ? values : new Dictionary<string, string>();

    /// <summary>
    /// A value as <c>QSettings</c> reads it back: quotes removed, escapes undone, and the space outside
    /// quotes trimmed.
    /// </summary>
    private static string Unescaped(string written)
    {
        var value = new StringBuilder(written.Length);
        var quoted = false;

        // The length of the value up to the last character that was quoted or escaped, below which an
        // outer space is part of the value rather than the file's layout.
        var kept = 0;

        for (var i = 0; i < written.Length; i++)
        {
            var c = written[i];

            if (c == '"')
            {
                quoted = !quoted;
                kept = value.Length;
                continue;
            }

            if (c != '\\' || i + 1 == written.Length)
            {
                // The space between the equals sign and the value is the file's layout too.
                if (!quoted && value.Length == 0 && char.IsWhiteSpace(c))
                {
                    continue;
                }

                value.Append(c);

                if (quoted || !char.IsWhiteSpace(c))
                {
                    kept = value.Length;
                }

                continue;
            }

            var escape = written[++i];

            if (escape is 'x' or 'X')
            {
                var digits = 0;

                while (digits < 4 && i + 1 + digits < written.Length && char.IsAsciiHexDigit(written[i + 1 + digits]))
                {
                    digits++;
                }

                if (digits > 0)
                {
                    value.Append((char)int.Parse(written.AsSpan(i + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += digits;
                }
            }
            else
            {
                value.Append(escape switch
                {
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'v' => '\v',
                    '0' => '\0',
                    _ => escape,
                });
            }

            kept = value.Length;
        }

        return value.ToString(0, kept);
    }
}
