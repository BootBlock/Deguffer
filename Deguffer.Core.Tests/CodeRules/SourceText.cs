using System.Text;

namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// A C# file with the parts a rule does not read blanked to spaces.
///
/// <para>Lengths and line breaks are kept, so a match's line number is the line in the file. A rule
/// about code must not match a doc comment that names the forbidden call to explain why it is
/// forbidden, or a string that quotes it, such as a PowerShell script holding <c>catch {</c>; a rule
/// about a retyped sentence must read the literals and still skip the comments.
/// <see cref="ProductSources.Code"/> drops whole comment lines only, which misses a trailing comment
/// and every literal.</para>
///
/// <para>An interpolated string is read as a literal from end to end, holes included. A hole is code,
/// but none of the rules looks for anything a hole could hold that the surrounding code does not.</para>
/// </summary>
internal static class SourceText
{
    public static string Mask(string source, SourceView view)
    {
        if (view == SourceView.Everything)
        {
            return source;
        }

        var text = new StringBuilder(source);
        var keepLiterals = view == SourceView.CodeAndLiterals;
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                i = Blank(text, i, LineEnd(source, i));
            }
            else if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = Blank(text, i, end < 0 ? source.Length : end + 2);
            }
            else if (IsLiteralStart(source, i, out var prefix))
            {
                var end = LiteralEnd(source, i, prefix);
                i = keepLiterals ? end : Blank(text, i, end);
            }
            else
            {
                i++;
            }
        }

        return text.ToString();
    }

    private static int LineEnd(string source, int from)
    {
        var end = source.IndexOf('\n', from);
        return end < 0 ? source.Length : end;
    }

    /// <summary>Blanks <c>[from, to)</c> but keeps its line breaks, and returns <paramref name="to"/>.</summary>
    private static int Blank(StringBuilder text, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (text[i] is not ('\n' or '\r'))
            {
                text[i] = ' ';
            }
        }

        return to;
    }

    /// <summary>
    /// Whether a literal starts at <paramref name="i"/>, with the length of the <c>$</c> and <c>@</c>
    /// prefix before its first quote. A character literal has no prefix.
    /// </summary>
    private static bool IsLiteralStart(string source, int i, out int prefix)
    {
        prefix = 0;

        while (i + prefix < source.Length && source[i + prefix] is '$' or '@')
        {
            prefix++;
        }

        return i + prefix < source.Length
            && (source[i + prefix] == '"' || (prefix == 0 && source[i] == '\''));
    }

    private static int LiteralEnd(string source, int start, int prefix)
    {
        var quote = start + prefix;

        if (source[quote] == '\'')
        {
            return QuotedEnd(source, quote + 1, '\'');
        }

        var quotes = 0;
        while (quote + quotes < source.Length && source[quote + quotes] == '"')
        {
            quotes++;
        }

        // Three or more quotes open a raw literal, closed by the same run; two are an empty string.
        if (quotes >= 3)
        {
            var close = source.IndexOf(new string('"', quotes), quote + quotes, StringComparison.Ordinal);
            return close < 0 ? source.Length : close + quotes;
        }

        if (quotes == 2)
        {
            return quote + 2;
        }

        var verbatim = source.AsSpan(start, prefix).Contains('@');
        return verbatim ? VerbatimEnd(source, quote + 1) : QuotedEnd(source, quote + 1, '"');
    }

    private static int QuotedEnd(string source, int from, char quote)
    {
        for (var i = from; i < source.Length; i++)
        {
            if (source[i] == '\\')
            {
                i++;
            }
            else if (source[i] == quote || source[i] == '\n')
            {
                return i + 1;
            }
        }

        return source.Length;
    }

    /// <summary>A verbatim string ends at a quote that is not doubled.</summary>
    private static int VerbatimEnd(string source, int from)
    {
        for (var i = from; i < source.Length; i++)
        {
            if (source[i] != '"')
            {
                continue;
            }

            if (i + 1 < source.Length && source[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i + 1;
        }

        return source.Length;
    }
}
