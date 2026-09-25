using System.Text;

namespace Deguffer.Core.Providers;

/// <summary>
/// One entry of a Steam key-values file: a key with either a text value or a block of entries.
/// </summary>
/// <param name="Key">The key, as written.</param>
/// <param name="Value">The text value, or null where the key opens a block.</param>
/// <param name="Children">The block's entries, empty where the key has a text value.</param>
internal sealed record SteamKeyValue(string Key, string? Value, IReadOnlyList<SteamKeyValue> Children)
{
    /// <summary>
    /// The first entry of this block called <paramref name="key"/>, or null. Keys are compared
    /// without regard to case because Steam's own reader does, and has written the same file's root
    /// key as both <c>LibraryFolders</c> and <c>libraryfolders</c> over the years.
    /// </summary>
    public SteamKeyValue? Child(string key) =>
        Children.FirstOrDefault(child => string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads Valve's text key-values format, the one Steam writes <c>libraryfolders.vdf</c> and every
/// <c>appmanifest_*.acf</c> in.
///
/// <para><b>Strict, because a file read wrongly names the wrong folder.</b> Anything this does not
/// understand makes the whole file unreadable rather than partly read: an unterminated string, a
/// key with no value, an unbalanced brace, or nesting past <see cref="MaximumDepth"/>. A caller then
/// says it could not read the file, which is true, where a lenient reader would hand back half a
/// list of libraries and a plan that looked complete.</para>
///
/// <para>Escapes follow Steam's own writer, which doubles every backslash in a path. <c>\\</c>,
/// <c>\"</c>, <c>\n</c> and <c>\t</c> are read as escapes, and any other backslash is kept as
/// written, so a path in a hand-edited file that was never escaped still reads as the path it
/// names. A <c>[$WIN32]</c>-style condition after a value is skipped, and so is a <c>//</c>
/// comment.</para>
/// </summary>
internal static class SteamKeyValues
{
    /// <summary>
    /// Deeper than Steam writes by a wide margin (its files nest four levels), and shallow enough
    /// that a hostile file cannot exhaust the stack through the recursion below.
    /// </summary>
    private const int MaximumDepth = 32;

    /// <summary>The file's top-level entries, or null where the text is not well-formed.</summary>
    public static IReadOnlyList<SteamKeyValue>? Parse(string text) =>
        new Reader(text).ReadBlock(depth: 0, closedByBrace: false);

    private sealed class Reader(string text)
    {
        private int _position;

        /// <summary>
        /// The entries up to the closing brace, or to the end of the text at the top level, or null
        /// where what is there is not well-formed.
        /// </summary>
        public List<SteamKeyValue>? ReadBlock(int depth, bool closedByBrace)
        {
            if (depth > MaximumDepth)
            {
                return null;
            }

            var entries = new List<SteamKeyValue>();

            while (true)
            {
                var token = Next();

                switch (token.Kind)
                {
                    case TokenKind.End:
                        return closedByBrace ? null : entries;

                    case TokenKind.Close:
                        return closedByBrace ? entries : null;

                    case TokenKind.Open or TokenKind.Malformed:
                        return null;
                }

                var value = Next();

                switch (value.Kind)
                {
                    case TokenKind.Text:
                        entries.Add(new SteamKeyValue(token.Text, value.Text, []));
                        break;

                    case TokenKind.Open:
                        if (ReadBlock(depth + 1, closedByBrace: true) is not { } children)
                        {
                            return null;
                        }

                        entries.Add(new SteamKeyValue(token.Text, null, children));
                        break;

                    default:
                        return null;
                }
            }
        }

        private Token Next()
        {
            while (true)
            {
                SkipWhitespaceAndComments();

                if (_position >= text.Length)
                {
                    return new Token(TokenKind.End, "");
                }

                switch (text[_position])
                {
                    case '{':
                        _position++;
                        return new Token(TokenKind.Open, "");

                    case '}':
                        _position++;
                        return new Token(TokenKind.Close, "");

                    case '"':
                        return Quoted();

                    case '[':
                        // A platform condition belongs to the entry before it. Steam does not write
                        // one into the files read here, so it is passed over rather than evaluated.
                        var close = text.IndexOf(']', _position);

                        if (close < 0)
                        {
                            return new Token(TokenKind.Malformed, "");
                        }

                        _position = close + 1;
                        continue;

                    default:
                        return Unquoted();
                }
            }
        }

        private void SkipWhitespaceAndComments()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position]))
                {
                    _position++;
                }
                else if (text[_position] == '/' && _position + 1 < text.Length && text[_position + 1] == '/')
                {
                    var end = text.IndexOf('\n', _position);
                    _position = end < 0 ? text.Length : end + 1;
                }
                else
                {
                    return;
                }
            }
        }

        private Token Quoted()
        {
            var builder = new StringBuilder();
            _position++;

            while (_position < text.Length)
            {
                var c = text[_position++];

                if (c == '"')
                {
                    return new Token(TokenKind.Text, builder.ToString());
                }

                if (c == '\\' && _position < text.Length)
                {
                    var escaped = text[_position];

                    switch (escaped)
                    {
                        case '\\' or '"':
                            builder.Append(escaped);
                            _position++;
                            continue;

                        case 'n':
                            builder.Append('\n');
                            _position++;
                            continue;

                        case 't':
                            builder.Append('\t');
                            _position++;
                            continue;
                    }
                }

                builder.Append(c);
            }

            return new Token(TokenKind.Malformed, "");
        }

        private Token Unquoted()
        {
            var start = _position;

            while (_position < text.Length
                   && !char.IsWhiteSpace(text[_position])
                   && text[_position] is not ('{' or '}' or '"' or '['))
            {
                _position++;
            }

            return new Token(TokenKind.Text, text[start.._position]);
        }
    }

    private enum TokenKind
    {
        Text,
        Open,
        Close,
        End,
        Malformed,
    }

    private readonly record struct Token(TokenKind Kind, string Text);
}
