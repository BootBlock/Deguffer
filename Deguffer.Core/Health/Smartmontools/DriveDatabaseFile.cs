using System.Text;

namespace Deguffer.Core.Health.Smartmontools;

/// <summary>One entry of a smartmontools drive database, as its five strings.</summary>
/// <param name="Family">The model family, or a marker: <c>VERSION:</c>, <c>DEFAULT</c>, <c>USB:</c> or a leading <c>$</c>.</param>
/// <param name="ModelPattern">A POSIX extended regular expression the whole model string must match.</param>
/// <param name="FirmwarePattern">The same for the firmware revision, where empty matches any.</param>
/// <param name="Presets">smartctl options, of which <c>-v</c> describes an attribute.</param>
internal sealed record DriveDatabaseEntry(string Family, string ModelPattern, string FirmwarePattern, string Presets);

/// <summary>
/// Reads the file format smartmontools' <c>drivedb.h</c> and <c>drivedb-add.h</c> share: a C array
/// initialiser of five-string entries, with comments, and string literals that join when only space or
/// a comment stands between them. The format is read here from its description and the file itself.
///
/// <para>One fault fails the whole file, as it fails smartctl's own reading of it: after a fault, where
/// one entry ends and the next starts is a guess, and an entry read out of line is another family's
/// meaning put on this drive.</para>
/// </summary>
internal static class DriveDatabaseFile
{
    private const int FieldsPerEntry = 5;

    /// <summary>The file's entries in order, or false with the first line that could not be read.</summary>
    internal static bool TryRead(string text, out IReadOnlyList<DriveDatabaseEntry> entries, out int failedLine)
    {
        var reader = new Cursor(text);
        var read = new List<DriveDatabaseEntry>();
        entries = read;
        failedLine = 0;

        while (reader.NextToken() is var token && token.Kind is not TokenKind.End)
        {
            if (token.Kind is not TokenKind.Open || ReadFields(ref reader) is not { } fields)
            {
                failedLine = reader.Line;
                return false;
            }

            // The fourth field is a message smartctl prints about a family's firmware, which this page does not show.
            read.Add(new DriveDatabaseEntry(fields[0], fields[1], fields[2], fields[4]));

            if (reader.NextToken() is { Kind: not TokenKind.Comma and not TokenKind.End } after)
            {
                failedLine = after.Line;
                return false;
            }
        }

        return true;
    }

    /// <summary>The five strings after an entry's opening brace, through its closing brace.</summary>
    private static string[]? ReadFields(ref Cursor reader)
    {
        var fields = new string[FieldsPerEntry];

        for (var field = 0; field < FieldsPerEntry; field++)
        {
            if (reader.NextToken() is not { Kind: TokenKind.Text, Text: { } text })
            {
                return null;
            }

            fields[field] = text;
            var next = reader.NextToken();

            if (field < FieldsPerEntry - 1 && next.Kind is not TokenKind.Comma)
            {
                return null;
            }

            // The last string may be followed by a comma before the closing brace.
            if (field == FieldsPerEntry - 1 && (next.Kind is TokenKind.Comma ? reader.NextToken() : next).Kind is not TokenKind.Close)
            {
                return null;
            }
        }

        return fields;
    }

    private enum TokenKind
    {
        End,
        Open,
        Close,
        Comma,
        Text,
        Fault,
    }

    private readonly record struct Token(TokenKind Kind, int Line, string? Text = null);

    private struct Cursor(string text)
    {
        private int _at;
        private int _line = 1;

        /// <summary>The line of the token read last, which is where a fault in the entry's shape is reported.</summary>
        internal int Line { get; private set; }

        internal Token NextToken()
        {
            var token = Next();
            Line = token.Line;
            return token;
        }

        private Token Next()
        {
            if (!SkipSpace())
            {
                return Fault();
            }

            if (_at >= text.Length)
            {
                return new Token(TokenKind.End, _line);
            }

            var line = _line;

            switch (text[_at])
            {
                case '{':
                    _at++;
                    return new Token(TokenKind.Open, line);
                case '}':
                    _at++;
                    return new Token(TokenKind.Close, line);
                case ',':
                    _at++;
                    return new Token(TokenKind.Comma, line);
                case '"':
                    return JoinedText(line);
                default:
                    return Fault();
            }
        }

        /// <summary>A string literal and every literal joined to it by space or comments alone.</summary>
        private Token JoinedText(int line)
        {
            var joined = new StringBuilder();

            do
            {
                if (!Literal(joined) || !SkipSpace())
                {
                    return Fault();
                }
            }
            while (_at < text.Length && text[_at] == '"');

            return new Token(TokenKind.Text, line, joined.ToString());
        }

        /// <summary>One literal from its opening quote, with the escapes the format uses.</summary>
        private bool Literal(StringBuilder into)
        {
            for (_at++; _at < text.Length; _at++)
            {
                var c = text[_at];

                if (c == '"')
                {
                    _at++;
                    return true;
                }

                if (c is '\n' or '\r')
                {
                    return false;
                }

                if (c != '\\')
                {
                    into.Append(c);
                    continue;
                }

                if (++_at >= text.Length)
                {
                    return false;
                }

                switch (text[_at])
                {
                    case 'n':
                        into.Append('\n');
                        break;
                    case '\\' or '"':
                        into.Append(text[_at]);
                        break;
                    case '\r' when _at + 1 < text.Length && text[_at + 1] == '\n':
                        // A backslash before the end of a line carries the literal on to the next.
                        _at++;
                        _line++;
                        break;
                    case '\n':
                        _line++;
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }

        /// <summary>Moves past space and comments. False where a block comment is never closed.</summary>
        private bool SkipSpace()
        {
            while (_at < text.Length)
            {
                var c = text[_at];

                if (c == '\n')
                {
                    _line++;
                    _at++;
                }
                else if (c is ' ' or '\t' or '\r')
                {
                    _at++;
                }
                else if (c == '/' && _at + 1 < text.Length && text[_at + 1] == '/')
                {
                    var end = text.IndexOf('\n', _at);
                    _at = end < 0 ? text.Length : end;
                }
                else if (c == '/' && _at + 1 < text.Length && text[_at + 1] == '*')
                {
                    var end = text.IndexOf("*/", _at + 2, StringComparison.Ordinal);

                    if (end < 0)
                    {
                        return false;
                    }

                    _line += text.AsSpan(_at, end - _at).Count('\n');
                    _at = end + 2;
                }
                else
                {
                    return true;
                }
            }

            return true;
        }

        private readonly Token Fault() => new(TokenKind.Fault, _line);
    }
}
