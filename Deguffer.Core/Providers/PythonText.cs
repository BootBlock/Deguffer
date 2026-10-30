namespace Deguffer.Core.Providers;

/// <summary>
/// Python's <c>str.splitlines</c> and <c>str.strip</c>, for reading a file as a tool written in
/// Python reads it. .NET's own splitting and trimming disagree with both: they miss the information
/// separators, and splitting on each break character alone makes two lines of a carriage return and
/// line feed, so a line counted that way is not the line Python reports.
/// </summary>
internal static class PythonText
{
    /// <summary>What Python's <c>str.isspace</c> is true for: Unicode's white space and the four information separators.</summary>
    private const string WhiteSpace =
        "\u0009\u000a\u000b\u000c\u000d\u001c\u001d\u001e\u001f\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003"
        + "\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";

    /// <summary>
    /// <c>str.splitlines</c>: a break is a line feed, a carriage return, the two together, a vertical
    /// tab, a form feed, a file, group or record separator, a next line, or a line or paragraph
    /// separator, and a break at the very end makes no empty line after it.
    /// </summary>
    public static IEnumerable<string> Lines(string text)
    {
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029'))
            {
                continue;
            }

            yield return text[start..i];

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    /// <summary><c>str.strip</c> with no argument, which takes the information separators as well as what .NET calls white space.</summary>
    public static string Strip(string text) => text.AsSpan().Trim(WhiteSpace).ToString();
}
