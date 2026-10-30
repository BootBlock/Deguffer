using System.Text;
using Deguffer.Core.Providers;

namespace Deguffer.Core.Safety;

/// <summary>
/// A file a tool writes into a directory it made, and the lines that prove the tool wrote it.
///
/// <para>Where <see cref="BuildDirectoryKind.RequiredContents"/> asks only that a name is there, this
/// reads the file. A name is something anybody can create, and the cache directory tag is the case
/// that showed it: the tagging convention is shared by many tools and by people, who tag a folder by
/// hand to keep it out of a backup, so a file called <c>CACHEDIR.TAG</c> says only that somebody
/// called this folder disposable. What its writer put inside is what says who.</para>
///
/// <para>Lines are compared exactly, one by one from the first, with a line ending of either
/// <c>\n</c> or <c>\r\n</c> accepted. Both are what the tools themselves write on Windows: pytest,
/// Cargo and ruff write bytes, and mypy, tox and nox write through Python's text mode, which
/// translates each <c>\n</c>. Nothing else is relaxed: no trimming, no case folding, and a byte
/// order mark is part of the first line, so it fails the comparison.</para>
/// </summary>
/// <param name="Name">The file's name, inside the directory.</param>
/// <param name="Lines">The lines the file must open with, in order.</param>
/// <param name="IsWholeFile">
/// Whether <paramref name="Lines"/> are the whole file, for a tool whose marker carries no line of
/// its own. ruff's tag is the bare 43-byte header, and what tells it apart from a tag anybody wrote is
/// that nothing else is in it — together with the <c>.gitignore</c> beside it naming ruff.
/// </param>
public sealed record MarkerFile(string Name, IReadOnlyList<string> Lines, bool IsWholeFile = false)
{
    /// <summary>The name the Cache Directory Tagging Specification gives the tag.</summary>
    public const string CacheDirectoryTagName = "CACHEDIR.TAG";

    /// <summary>
    /// The specification's header, which "must" be the tag's first 43 octets
    /// (<see href="https://bford.info/cachedir/"/>). Compared here as a whole first line, which is
    /// stricter than the specification by one byte: whatever follows the header must be a line ending
    /// or the end of the file. Every tool this is declared for writes one or the other.
    /// </summary>
    public const string CacheDirectoryTagHeader = "Signature: 8a477f597d28d172789f06886806bc55";

    /// <summary>
    /// Far beyond any marker declared here, the longest being pytest's tag at under 200 bytes. A file past
    /// it is not the marker it is named for, and is not read whole to find that out.
    /// </summary>
    private const int MaximumBytes = 4096;

    /// <summary>A cache directory tag opening with the header and then each of <paramref name="producerLines"/>.</summary>
    public static MarkerFile CacheDirectoryTag(params string[] producerLines) =>
        new(CacheDirectoryTagName, [CacheDirectoryTagHeader, .. producerLines]);

    /// <summary>
    /// A cache directory tag opening with the header, whoever wrote it. For a rule whose other evidence
    /// says which tool this is, as a <c>Cargo.toml</c> beside a <c>target</c> does.
    /// </summary>
    public static MarkerFile AnyCacheDirectoryTag { get; } =
        new(CacheDirectoryTagName, [CacheDirectoryTagHeader]);

    /// <summary>A cache directory tag holding the header and nothing else.</summary>
    public static MarkerFile BareCacheDirectoryTag { get; } =
        new(CacheDirectoryTagName, [CacheDirectoryTagHeader], IsWholeFile: true);

    /// <summary>
    /// Whether this marker is in <paramref name="directory"/> as its tool writes it.
    ///
    /// <para>Two answers, never three. The marker decides what the directory <em>is</em>, and a file
    /// Windows would not let Deguffer read is no evidence that it is anything, so a refusal is
    /// "not recognised" and the directory is left alone. A link is refused too, as the specification
    /// requires of a tag: a link's target is somewhere else's file, and says nothing about this
    /// directory.</para>
    /// </summary>
    public bool IsIn(string directory)
    {
        var path = Path.Combine(directory, Name);

        if (LongPath.IsReparsePoint(path) || BoundedFile.ReadVerbatim(path, MaximumBytes) is not { } content)
        {
            return false;
        }

        string text;

        try
        {
            text = BoundedFile.StrictUtf8.GetString(content.Span);
        }
        catch (DecoderFallbackException)
        {
            // Not text at all, so not anything a tool declared here wrote.
            return false;
        }

        var lines = SplitLines(text);

        return IsWholeFile
            ? lines.SequenceEqual(Lines, StringComparer.Ordinal)
            : lines.Count >= Lines.Count && lines.Take(Lines.Count).SequenceEqual(Lines, StringComparer.Ordinal);
    }

    /// <summary>
    /// The file's lines, each without its <c>\n</c> or <c>\r\n</c>, and with no empty last line where
    /// the file ends in a line ending.
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        var lines = text.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToList();

        if (lines.Count > 0 && text.EndsWith('\n'))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
