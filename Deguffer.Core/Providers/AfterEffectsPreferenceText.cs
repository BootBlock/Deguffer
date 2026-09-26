using System.Globalization;
using System.Text;

namespace Deguffer.Core.Providers;

/// <summary>
/// Reads values out of an After Effects preferences file: the text files After Effects keeps in
/// <c>%APPDATA%\Adobe\After Effects\&lt;version&gt;</c>, which begin <c># Text File Version 1.1</c>.
///
/// <para><b>The format, as the files show it.</b> A section is a line <c>["name"]</c>, and each line
/// after it is <c>"key" = value</c>. A key or a value is a run of quoted fragments and hexadecimal
/// bytes: <c>"After Effects Classic Dark"1F"Basic Light"</c> is two names with byte 0x1F between them,
/// and a Russian install writes <c>"Simple.Coachmark."D09FD180...</c> for text in Cyrillic, so the
/// bytes are UTF-8 and the file itself is plain ASCII. A long line is split by ending it with a
/// backslash outside the quotes and carrying on, indented, on the next. Line ends are whatever the
/// platform wrote: a carriage return alone on macOS, a line feed or both on Windows.</para>
///
/// <para><b>Nothing here guesses.</b> A line this cannot read, or bytes that are not UTF-8, gives no
/// value rather than a near miss, because the one value Deguffer reads names a folder it will look
/// in for something to delete.</para>
/// </summary>
public static class AfterEffectsPreferenceText
{
    private const char Quote = '"';
    private const char Continuation = '\\';

    /// <summary>
    /// Every value in <paramref name="section"/> whose key <paramref name="wanted"/> accepts, with its
    /// key, in the order the file gives them. A key or value that cannot be read is left out.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> Values(string text, string section, Func<string, bool> wanted)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(wanted);

        var found = new List<(string, string)>();
        var inSection = false;

        foreach (var line in LogicalLines(text))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSection = Decode(trimmed[1..^1]) is { } name && name.Equals(section, StringComparison.Ordinal);
                continue;
            }

            if (inSection && Entry(trimmed) is { } entry && wanted(entry.Key))
            {
                found.Add(entry);
            }
        }

        return found;
    }

    /// <summary>
    /// The file's lines with every continuation joined, so a value split across lines reads as one.
    /// A line continues where it ends in a backslash outside the quotes; a backslash inside them is a
    /// path's own separator, and a value such as <c>"D:\"</c> ends in a quote.
    /// </summary>
    private static IEnumerable<string> LogicalLines(string text)
    {
        var line = new StringBuilder();

        foreach (var physical in text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None))
        {
            var part = line.Length == 0 ? physical : physical.TrimStart();

            if (EndsInContinuation(part))
            {
                line.Append(part, 0, part.Length - 1);
                continue;
            }

            line.Append(part);
            yield return line.ToString();
            line.Clear();
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static bool EndsInContinuation(string part)
    {
        var quoted = false;

        foreach (var c in part)
        {
            quoted ^= c == Quote;
        }

        return !quoted && part.EndsWith(Continuation);
    }

    /// <summary>One <c>"key" = value</c> line, or null where the line is not one.</summary>
    private static (string Key, string Value)? Entry(string line)
    {
        // The key is quoted, so the first " = " outside the quotes is the one after it.
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            quoted ^= line[i] == Quote;

            if (!quoted && string.CompareOrdinal(line, i, " = ", 0, 3) == 0)
            {
                return Decode(line[..i]) is { } key && Decode(line[(i + 3)..]) is { } value
                    ? (key, value)
                    : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The text a run of quoted fragments and hexadecimal bytes spells, or null where the run is
    /// anything else or its bytes are not UTF-8.
    /// </summary>
    private static string? Decode(string encoded)
    {
        var bytes = new List<byte>(encoded.Length);
        var i = 0;

        while (i < encoded.Length)
        {
            if (encoded[i] == Quote)
            {
                var close = encoded.IndexOf(Quote, i + 1);

                if (close < 0)
                {
                    return null;
                }

                foreach (var c in encoded.AsSpan(i + 1, close - i - 1))
                {
                    // A byte outside ASCII is written in hexadecimal, never inside the quotes.
                    if (c > 0x7F)
                    {
                        return null;
                    }

                    bytes.Add((byte)c);
                }

                i = close + 1;
            }
            else if (i + 1 < encoded.Length && char.IsAsciiHexDigit(encoded[i]) && char.IsAsciiHexDigit(encoded[i + 1]))
            {
                bytes.Add(byte.Parse(encoded.AsSpan(i, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                i += 2;
            }
            else
            {
                return null;
            }
        }

        try
        {
            return StrictUtf8.GetString([.. bytes]);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
