using System.Text.RegularExpressions;

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
public sealed partial record RegistryFileContent(string FirstKey, string? DisplayName, bool IsConfined)
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
                // A hex value's data carried onto the next line. reg.exe joins the next line to the
                // value whatever it holds, so one that is not hex digits and commas is not data this
                // can vouch for.
                var more = HexContinuation().Match(line);
                confined &= more.Success;
                continued = more.Success && more.Groups["more"].Success;
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

            // Only the forms reg.exe writes, and a deletion (=-) is never one. Anything else is
            // data whose reading this cannot vouch for: a string value ending in a backslash is
            // not continued by reg.exe, so a section on the next line would be imported unseen.
            if (StringData(data) is { } stringValue)
            {
                if (inFirstSection && value.Name.Equals("DisplayName", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = stringValue;
                }
            }
            else if (HexData().Match(data) is { Success: true } hex)
            {
                continued = hex.Groups["more"].Success;
            }
            else if (!DwordData().IsMatch(data))
            {
                confined = false;
            }
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

    /// <summary>
    /// A string value's text, or null where the data is not exactly one quoted string: its closing
    /// quote unescaped and the last character on the line.
    /// </summary>
    private static string? StringData(string data)
    {
        if (data.Length < 2 || data[0] != '"')
        {
            return null;
        }

        for (var at = 1; at < data.Length; at++)
        {
            if (data[at] == '\\')
            {
                at++;
            }
            else if (data[at] == '"')
            {
                return at == data.Length - 1 ? Unescape(data[1..at]) : null;
            }
        }

        return null;
    }

    /// <summary><c>dword:</c> and eight hex digits.</summary>
    [GeneratedRegex("^dword:[0-9a-f]{8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DwordData();

    /// <summary>
    /// <c>hex:</c> or <c>hex(type):</c> and comma-separated bytes, with <c>more</c> where a trailing
    /// backslash carries the data onto the next line.
    /// </summary>
    [GeneratedRegex(@"^hex(?:\([0-9a-f]{1,8}\))?:(?:[0-9a-f]{2}(?:,[0-9a-f]{2})*)?(?<more>,?\\)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HexData();

    /// <summary>A continued line of hex data: indented bytes, with <c>more</c> where it continues again.</summary>
    [GeneratedRegex(@"^\s*[0-9a-f]{2}(?:,[0-9a-f]{2})*(?<more>,\\)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HexContinuation();

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
