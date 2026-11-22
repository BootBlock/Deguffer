using System.Text;
using System.Text.RegularExpressions;

namespace Deguffer.Core.Health.Smartmontools;

/// <summary>
/// A POSIX extended regular expression from a drive database, as a .NET pattern that must match a
/// whole string, as smartctl matches a model or firmware.
///
/// <para>Only the part of the syntax whose meaning is the same in both is translated: literals, any
/// character, bracket expressions of characters and ranges, groups, alternation, the repeats and the
/// anchors. A character class such as <c>[[:digit:]]</c>, an escaped letter or a stray brace has no
/// translation here, so the pattern is refused rather than read as something it does not say. Which
/// strings a pattern matches as a whole does not depend on POSIX's longest-match rule, so .NET's
/// engine answers the same.</para>
///
/// <para>Built at run time, because the patterns are the file's, and each is built once for the life
/// of the database read. The file is not Deguffer's, so each match has a time limit, and a match that
/// reaches it answers nothing. The non-backtracking engine would need no limit, and was measured in
/// 2026 to take over a second to build the patterns a search passes on the way to one hard disk's
/// entry, where this engine took under 50 ms.</para>
/// </summary>
internal static class PosixPattern
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Singleline;

    /// <summary>Thousands of times what a model string takes against any pattern in the file.</summary>
    private static readonly TimeSpan MatchLimit = TimeSpan.FromMilliseconds(250);

    /// <summary>The pattern as a whole-string matcher, or null where it uses syntax not translated here.</summary>
    internal static Regex? Compile(string pattern)
    {
        if (Translate(pattern) is not { } translated)
        {
            return null;
        }

        try
        {
            return new Regex($@"\A(?:{translated})\z", Options, MatchLimit);
        }
        catch (ArgumentException)
        {
            // Syntax POSIX leaves undefined, such as a repeat with nothing before it.
            return null;
        }
    }

    private static string? Translate(string pattern)
    {
        var into = new StringBuilder(pattern.Length + 8);

        for (var at = 0; at < pattern.Length; at++)
        {
            var c = pattern[at];

            switch (c)
            {
                case '\\':
                    // An escaped letter or digit means something else in each dialect, or nothing.
                    if (++at >= pattern.Length || char.IsLetterOrDigit(pattern[at]))
                    {
                        return null;
                    }

                    into.Append(Regex.Escape(pattern[at].ToString()));
                    break;
                case '[':
                    if (!Bracket(pattern, ref at, into))
                    {
                        return null;
                    }

                    break;
                case '{':
                    if (!Interval(pattern, ref at, into))
                    {
                        return null;
                    }

                    break;
                case '(' when at + 1 < pattern.Length && pattern[at + 1] == '?':
                    // A group construct in .NET, and a repeat with nothing before it in POSIX.
                    return null;
                case '(' or ')' or '|' or '*' or '+' or '?' or '.':
                    into.Append(c);
                    break;
                case '^':
                    into.Append(@"\A");
                    break;
                case '$':
                    into.Append(@"\z");
                    break;
                default:
                    into.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return into.ToString();
    }

    /// <summary>
    /// A bracket expression from its opening bracket. In POSIX a backslash is literal inside one, and a
    /// closing bracket first is a member, so each member is escaped for .NET one by one.
    /// </summary>
    private static bool Bracket(string pattern, ref int at, StringBuilder into)
    {
        var start = at + 1;
        var negated = start < pattern.Length && pattern[start] == '^';
        var first = negated ? start + 1 : start;
        var end = pattern.IndexOf(']', first < pattern.Length && pattern[first] == ']' ? first + 1 : first);

        if (end < 0)
        {
            return false;
        }

        var members = pattern.AsSpan(first, end - first);

        // Character classes, equivalence classes and collating symbols.
        if (members.Contains("[:", StringComparison.Ordinal)
            || members.Contains("[=", StringComparison.Ordinal)
            || members.Contains("[.", StringComparison.Ordinal))
        {
            return false;
        }

        into.Append(negated ? "[^" : "[");

        for (var i = 0; i < members.Length; i++)
        {
            // A hyphen between two members is a range. First or last, it is itself.
            var isRange = members[i] == '-' && i > 0 && i < members.Length - 1;
            into.Append(isRange ? "-" : Member(members[i]));
        }

        into.Append(']');
        at = end;
        return true;
    }

    private static string Member(char c) => c is '\\' or ']' or '[' or '^' or '-' ? $"\\{c}" : c.ToString();

    /// <summary>A bounded repeat, <c>{m}</c>, <c>{m,}</c> or <c>{m,n}</c>, which both dialects write alike.</summary>
    private static bool Interval(string pattern, ref int at, StringBuilder into)
    {
        var end = pattern.IndexOf('}', at);

        if (end < 0)
        {
            return false;
        }

        var bounds = pattern.AsSpan(at + 1, end - at - 1);
        var comma = bounds.IndexOf(',');
        var low = comma < 0 ? bounds : bounds[..comma];
        var high = comma < 0 ? [] : bounds[(comma + 1)..];

        if (low.IsEmpty || !IsDigits(low) || !IsDigits(high))
        {
            return false;
        }

        into.Append(pattern, at, end - at + 1);
        at = end;
        return true;
    }

    private static bool IsDigits(ReadOnlySpan<char> text) => !text.ContainsAnyExceptInRange('0', '9');
}
