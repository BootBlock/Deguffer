using System.Text;

namespace Deguffer.Core.Tests;

/// <summary>
/// One of the repository's agent-facing markdown guides, read from the tree rather than from a
/// copy in the test output, so a test asserts against the file an agent will actually be given.
/// </summary>
internal sealed class MarkdownGuide
{
    private readonly string _text;

    private MarkdownGuide(string name, string text)
    {
        Name = name;
        _text = text;
    }

    public string Name { get; }

    public string Text => _text;

    public static MarkdownGuide AtRepositoryRoot(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Deguffer.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"No directory above {AppContext.BaseDirectory} contains Deguffer.sln, so the "
                + "repository root could not be found.");

        var path = Path.Combine(directory!.FullName, fileName);
        Assert.True(File.Exists(path), $"{fileName} is missing from the repository root.");

        return new MarkdownGuide(fileName, File.ReadAllText(path));
    }

    /// <summary>
    /// The GitHub heading anchor for every "##" and "###" section, keyed by anchor. A heading
    /// inside a fenced code block is a shell comment, not a section.
    /// </summary>
    public IReadOnlyDictionary<string, string> HeadingAnchors()
    {
        var anchors = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in Lines(skipFencedBlocks: true))
        {
            if (!line.StartsWith("## ", StringComparison.Ordinal)
                && !line.StartsWith("### ", StringComparison.Ordinal))
            {
                continue;
            }

            var text = line.TrimStart('#').Trim();
            var anchor = Anchor(text);

            Assert.False(
                anchors.ContainsKey(anchor),
                $"Two {Name} headings share the anchor #{anchor}, so a link to it is ambiguous: "
                    + $"\"{anchors.GetValueOrDefault(anchor)}\" and \"{text}\".");

            anchors[anchor] = text;
        }

        return anchors;
    }

    public IEnumerable<string> Lines(bool skipFencedBlocks)
    {
        var fenced = false;

        foreach (var line in _text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (!fenced || !skipFencedBlocks)
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// GitHub's heading slug: lower-case, drop everything that is not a letter, a digit, a space,
    /// a hyphen or an underscore, then replace each remaining space with a hyphen.
    /// </summary>
    public static string Anchor(string heading)
    {
        var slug = new StringBuilder(heading.Length);

        foreach (var ch in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                slug.Append(ch);
            }
            else if (ch == ' ')
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }
}
