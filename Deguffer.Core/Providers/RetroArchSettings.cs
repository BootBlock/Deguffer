using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A <c>retroarch.cfg</c>, read by the grammar RetroArch's own <c>config_file.c</c> uses, which is not
/// the grammar of an INI file.
///
/// <para><b>Read as RetroArch reads it, because every folder it names is a setting.</b> Each of the
/// folders the RetroArch rows look in can be moved, and a reading that disagrees with RetroArch's own
/// would look for downloads in a folder RetroArch no longer uses, or miss the one it does. So the
/// rules below are RetroArch's, including the ones that look like mistakes:</para>
/// <list type="bullet">
/// <item>A line is split only at a line feed. A key is the run of printable ASCII after any leading
/// whitespace, and it is an entry only where whitespace and then <c>=</c> follow it:
/// <c>key=value</c> is not an entry.</item>
/// <item>A quoted value runs to the next quote, with no escapes, or to the end of the line. An unquoted
/// value stops at the first character outside printable ASCII, so at a space.</item>
/// <item>A <c>#</c> in the first column makes the line a comment, unless the line is an
/// <c>#include</c>. Anywhere else the line is cut at the first <c>#</c> that is not between the first
/// quote and the next.</item>
/// <item><b>The first entry for a key wins</b>, and an included file's entries stand where its
/// <c>#include</c> line stands. A relative include is relative to the including file, a missing one is
/// skipped, and includes nest no deeper than RetroArch allows.</item>
/// </list>
///
/// <para><b>Nothing read here is shown, logged or quoted</b> apart from the folder values the rows
/// ask for, on <see cref="BoundedFile"/>'s reasoning: the file also holds account names and passwords
/// for the services RetroArch signs in to.</para>
/// </summary>
internal sealed class RetroArchSettings
{
    /// <summary>
    /// Far past any file RetroArch writes, which runs to a few thousand short lines, and small enough
    /// that a file of some other kind by that name is refused rather than read.
    /// </summary>
    private const int MaximumBytes = 1024 * 1024;

    /// <summary>RetroArch's <c>MAX_INCLUDE_DEPTH</c>. The file itself is depth 0.</summary>
    private const int MaximumIncludeDepth = 16;

    private const string IncludeDirective = "#include ";

    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _unreadIncludes = [];

    private RetroArchSettings(string path) => Path = path;

    /// <summary>The file that was read, in display form.</summary>
    public string Path { get; }

    /// <summary>
    /// Included files that exist and could not be read. An entry in one of them could have decided a
    /// value, so a reading with any is not the whole of what RetroArch reads.
    /// </summary>
    public IReadOnlyList<string> UnreadIncludes => _unreadIncludes;

    /// <summary>
    /// The settings in <paramref name="path"/>, or null where the file itself could not be read or is
    /// larger than any file RetroArch writes.
    /// </summary>
    public static RetroArchSettings? Read(string path)
    {
        if (BoundedFile.Read(path, MaximumBytes) is not { } content)
        {
            return null;
        }

        var settings = new RetroArchSettings(path);
        settings.Parse(content.Span, path, depth: 0);

        return settings;
    }

    /// <summary>The value of <paramref name="key"/>, or null where no entry sets it. Keys are case-sensitive.</summary>
    public string? this[string key] => _entries.GetValueOrDefault(key);

    private void Parse(ReadOnlySpan<byte> content, string file, int depth)
    {
        foreach (var raw in Encoding.UTF8.GetString(content).Split('\n'))
        {
            if (raw.StartsWith(IncludeDirective, StringComparison.Ordinal))
            {
                Include(raw[IncludeDirective.Length..], file, depth);
                continue;
            }

            if (raw.StartsWith('#'))
            {
                continue;
            }

            if (Entry(WithoutComment(raw)) is var (key, value))
            {
                _entries.TryAdd(key, value);
            }
        }
    }

    private void Include(string rest, string file, int depth)
    {
        if (depth >= MaximumIncludeDepth || Value(rest.AsSpan()) is not { Length: > 0 } named)
        {
            return;
        }

        var included = System.IO.Path.IsPathFullyQualified(named)
            ? named
            : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file) ?? string.Empty, named);

        switch (LongPath.ProbeFile(included))
        {
            case PathPresence.Absent:
                return;

            case PathPresence.Refused:
                _unreadIncludes.Add(included);
                return;
        }

        if (BoundedFile.Read(included, MaximumBytes) is not { } content)
        {
            _unreadIncludes.Add(included);
            return;
        }

        Parse(content.Span, included, depth + 1);
    }

    /// <summary>
    /// The line cut at its first <c>#</c>, unless that <c>#</c> is between the first quote and the next.
    /// </summary>
    private static string WithoutComment(string line)
    {
        var hash = line.IndexOf('#');

        if (hash < 0)
        {
            return line;
        }

        var open = line.IndexOf('"');
        var close = open < 0 ? -1 : line.IndexOf('"', open + 1);

        return open >= 0 && close > open && hash > open && hash < close ? line : line[..hash];
    }

    private static (string Key, string Value)? Entry(string line)
    {
        var span = line.AsSpan().TrimStart(" \t\r\n");
        var keyLength = 0;

        while (keyLength < span.Length && IsPrintable(span[keyLength]))
        {
            keyLength++;
        }

        if (keyLength == 0)
        {
            return null;
        }

        var key = span[..keyLength].ToString();
        var rest = span[keyLength..];

        // The key must be followed by whitespace, and then by the equals sign.
        if (rest.IsEmpty || IsPrintable(rest[0]))
        {
            return null;
        }

        rest = rest.TrimStart(" \t");

        if (rest.IsEmpty || rest[0] != '=')
        {
            return null;
        }

        return Value(rest[1..]) is { } value ? (key, value) : null;
    }

    /// <summary>
    /// A value as RetroArch extracts one: quoted to the next quote or the end of the line, otherwise the
    /// run of printable ASCII. Empty where nothing follows.
    /// </summary>
    private static string? Value(ReadOnlySpan<char> text)
    {
        text = text.TrimStart(" \t");

        if (!text.IsEmpty && text[0] == '"')
        {
            var body = text[1..];
            var close = body.IndexOf('"');

            return (close < 0 ? body : body[..close]).ToString();
        }

        var length = 0;

        while (length < text.Length && IsPrintable(text[length]))
        {
            length++;
        }

        return text[..length].ToString();
    }

    private static bool IsPrintable(char c) => c is >= '!' and <= '~';
}
