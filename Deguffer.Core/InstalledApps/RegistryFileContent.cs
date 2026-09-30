namespace Deguffer.Core.InstalledApps;

/// <summary>
/// What a <c>.reg</c> file would do if <c>reg.exe import</c> ran it, read before it is run (§7.3).
///
/// <para><b>Every section counts, not the first.</b> An import applies the whole file: a second
/// section can write any key the importing process may write, and a <c>[-key]</c> header or a
/// <c>"name"=-</c> line deletes. A backup folder is writable by anything running as the user, so a
/// file that names one entry first proves nothing about what else it holds.</para>
/// </summary>
/// <param name="FirstKey">The key the first section names.</param>
/// <param name="DisplayName">The first section's <c>DisplayName</c>, or null where it records none.</param>
/// <param name="IsConfined">
/// Whether every section writes <see cref="FirstKey"/> or a key below it, and nothing is deleted.
/// Only such a file is a backup of one entry.
/// </param>
public sealed record RegistryFileContent(string FirstKey, string? DisplayName, bool IsConfined)
{
    private const string Header = "Windows Registry Editor Version 5.00";

    /// <summary>What <paramref name="text"/> would do, or null where it is not a registry file with a section.</summary>
    public static RegistryFileContent? Parse(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r'));
        string? firstKey = null;
        string? displayName = null;
        var confined = true;
        var sawHeader = false;
        var inFirstSection = false;
        var continued = false;

        foreach (var line in lines)
        {
            if (!sawHeader)
            {
                // The file's own first line, after any blank ones. Anything else is not reg.exe's format.
                if (line.Length == 0)
                {
                    continue;
                }

                if (!line.Equals(Header, StringComparison.Ordinal))
                {
                    return null;
                }

                sawHeader = true;
                continue;
            }

            if (continued)
            {
                // A value's data carried onto the next line: hex digits and commas, nothing that
                // opens a section or names a value.
                continued = line.EndsWith('\\');
                continue;
            }

            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']') || line.Length < 3)
                {
                    return null;
                }

                var key = line[1..^1];
                inFirstSection = firstKey is null;

                if (key.StartsWith('-'))
                {
                    confined = false;
                }
                else if (firstKey is null)
                {
                    firstKey = key;
                }
                else if (!key.Equals(firstKey, StringComparison.OrdinalIgnoreCase)
                    && !key.StartsWith(firstKey + @"\", StringComparison.OrdinalIgnoreCase))
                {
                    confined = false;
                }

                continue;
            }

            if (ValueName(line) is not { } value)
            {
                if (line.Trim().Length > 0)
                {
                    // A line that is neither a section, a value nor blank: reg.exe's reading of it
                    // is not one this can vouch for.
                    confined = false;
                }

                continue;
            }

            var data = line[value.End..];

            if (data.StartsWith('-'))
            {
                confined = false;
            }

            if (inFirstSection && value.Name.Equals("DisplayName", StringComparison.OrdinalIgnoreCase)
                && StringData(data) is { } name)
            {
                displayName = name;
            }

            continued = line.EndsWith('\\');
        }

        return firstKey is null ? null : new RegistryFileContent(firstKey, displayName, confined);
    }

    /// <summary>
    /// A value line's name and where its data starts after the <c>=</c>, or null where the line
    /// names no value. <c>@</c> is the default value; a quoted name escapes <c>\</c> and <c>"</c>.
    /// </summary>
    private static (string Name, int End)? ValueName(string line)
    {
        if (line.StartsWith("@=", StringComparison.Ordinal))
        {
            return (string.Empty, 2);
        }

        if (!line.StartsWith('"'))
        {
            return null;
        }

        for (var at = 1; at < line.Length; at++)
        {
            if (line[at] == '\\')
            {
                at++;
            }
            else if (line[at] == '"')
            {
                return at + 1 < line.Length && line[at + 1] == '='
                    ? (Unescape(line[1..at]), at + 2)
                    : null;
            }
        }

        return null;
    }

    /// <summary>A string value's text, or null where the data is not a quoted string.</summary>
    private static string? StringData(string data) =>
        data.Length >= 2 && data[0] == '"' && data[^1] == '"' ? Unescape(data[1..^1]) : null;

    /// <summary>One pass, left to right, as reg.exe writes the escapes: <c>\\</c> and <c>\"</c>.</summary>
    private static string Unescape(string value)
    {
        var text = new System.Text.StringBuilder(value.Length);

        for (var at = 0; at < value.Length; at++)
        {
            text.Append(value[at] == '\\' && at + 1 < value.Length ? value[++at] : value[at]);
        }

        return text.ToString();
    }
}
