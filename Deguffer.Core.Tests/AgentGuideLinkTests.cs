using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// The skills and plan docs link into CLAUDE.md by heading anchor. Its budgets mean its headings
/// get shortened and merged, and a renamed heading breaks every link to it with no error anywhere,
/// so the build follows each one.
/// </summary>
public sealed class AgentGuideLinkTests
{
    private static readonly Regex LinkIntoClaudeMd =
        new(@"\]\((?<target>[^)\s#]*CLAUDE\.md)#(?<anchor>[^)\s]+)\)", RegexOptions.Compiled);

    private static readonly Regex SelfAnchorLink =
        new(@"\]\(#(?<anchor>[^)\s]+)\)", RegexOptions.Compiled);

    // Build output and tool state, never a document an agent is pointed at.
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".drive-net", "!Distribution", "bin", "obj", "TestResults", "node_modules",
    };

    [Fact]
    public void EveryInternalLinkInClaudeMdResolves()
    {
        var claude = MarkdownGuide.AtRepositoryRoot("CLAUDE.md");
        var anchors = claude.HeadingAnchors();

        var dead = SelfAnchorLink
            .Matches(claude.Text)
            .Select(m => m.Groups["anchor"].Value)
            .Where(anchor => !anchors.ContainsKey(anchor))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            dead.Count == 0,
            "CLAUDE.md links to its own headings that do not exist: "
                + string.Join(", ", dead.Select(a => "#" + a)));
    }

    [Fact]
    public void EveryLinkIntoAClaudeMdHeadingResolves()
    {
        var anchorsByTarget = new Dictionary<string, IReadOnlyDictionary<string, string>?>(
            StringComparer.OrdinalIgnoreCase);
        var dead = new List<string>();
        var followed = 0;

        foreach (var path in MarkdownFiles(MarkdownGuide.RepositoryRoot))
        {
            var guide = MarkdownGuide.At(path);

            foreach (Match link in LinkIntoClaudeMd.Matches(guide.Text))
            {
                followed++;

                var target = Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(path)!, link.Groups["target"].Value));

                if (!anchorsByTarget.TryGetValue(target, out var anchors))
                {
                    anchors = File.Exists(target) ? MarkdownGuide.At(target).HeadingAnchors() : null;
                    anchorsByTarget[target] = anchors;
                }

                if (anchors is null || !anchors.ContainsKey(link.Groups["anchor"].Value))
                {
                    dead.Add($"{guide.Name}: {link.Value}");
                }
            }
        }

        // The skills link into CLAUDE.md, so following none means the sweep never reached them.
        Assert.True(followed > 0, "Found no link into a CLAUDE.md heading anywhere in the repository.");

        Assert.True(
            dead.Count == 0,
            "Links into CLAUDE.md headings that do not exist: " + string.Join(", ", dead));
    }

    private static IEnumerable<string> MarkdownFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(child)))
            {
                continue;
            }

            foreach (var file in MarkdownFiles(child))
            {
                yield return file;
            }
        }
    }
}
