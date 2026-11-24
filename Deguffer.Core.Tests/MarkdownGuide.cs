using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// One of the repository's agent-facing markdown files, read from the tree rather than from a copy
/// in the test output, so a test asserts against the file an agent will actually be given.
/// </summary>
internal sealed class MarkdownGuide
{
    private MarkdownGuide(string name, string text)
    {
        Name = name;
        Text = text;
    }

    /// <summary>The path relative to the repository root, for failure messages.</summary>
    public string Name { get; }

    /// <summary>
    /// The content with "\n" line endings, so a character count does not depend on whether git
    /// checked the file out with CRLF.
    /// </summary>
    public string Text { get; }

    public static MarkdownGuide AtRepositoryRoot(string fileName) =>
        At(Path.Combine(SourceTree.RepositoryRoot, fileName));

    public static MarkdownGuide At(string path)
    {
        var name = Path.GetRelativePath(SourceTree.RepositoryRoot, path);
        Assert.True(File.Exists(path), $"{name} does not exist.");

        return new MarkdownGuide(name, File.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// The GitHub heading anchor for every "##" and "###" section, keyed by anchor.
    /// </summary>
    public IReadOnlyDictionary<string, string> HeadingAnchors()
    {
        var anchors = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (line, prose) in ClassifiedLines())
        {
            if (!prose
                || (!line.StartsWith("## ", StringComparison.Ordinal)
                    && !line.StartsWith("### ", StringComparison.Ordinal)))
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

    /// <summary>
    /// Each "##" section's heading and its length in characters, from the heading up to the next
    /// "##" heading. A "###" subsection counts toward its parent, and content above the first "##"
    /// is not a section.
    /// </summary>
    public IReadOnlyList<(string Heading, int Length)> Sections()
    {
        var sections = new List<(string Heading, int Length)>();

        foreach (var (line, prose) in ClassifiedLines())
        {
            if (prose && line.StartsWith("## ", StringComparison.Ordinal))
            {
                sections.Add((line[3..].Trim(), 0));
            }

            if (sections.Count > 0)
            {
                var (heading, length) = sections[^1];
                sections[^1] = (heading, length + line.Length + 1);
            }
        }

        return sections;
    }

    /// <summary>
    /// GitHub's heading slug: lower-case, drop everything that is not a letter, a digit, a space,
    /// a hyphen or an underscore, then replace each remaining space with a hyphen.
    /// </summary>
    public static string Anchor(string heading)
    {
        var slug = new System.Text.StringBuilder(heading.Length);

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

    /// <summary>
    /// Every line, paired with whether it is prose. A fence delimiter and everything between a pair
    /// of them is not: a "#" line inside a fenced block is a shell comment, not a heading.
    /// </summary>
    private IEnumerable<(string Line, bool Prose)> ClassifiedLines()
    {
        var fenced = false;

        foreach (var line in Text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                yield return (line, false);
                continue;
            }

            yield return (line, !fenced);
        }
    }
}
