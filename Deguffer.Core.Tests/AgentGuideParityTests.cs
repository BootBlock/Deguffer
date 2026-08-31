using System.Text;
using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// AGENTS.md indexes every rule in CLAUDE.md, and an agent that reads only AGENTS.md is told the
/// index is complete. A rule added to CLAUDE.md without a row there is therefore invisible to that
/// agent while the file still claims otherwise. Review does not reliably catch the omission, so
/// the build does.
/// </summary>
public sealed class AgentGuideParityTests
{
    private static readonly Regex ClaudeAnchorLink =
        new(@"\(CLAUDE\.md#(?<anchor>[a-z0-9\-]+)\)", RegexOptions.Compiled);

    private static readonly Regex SelfAnchorLink =
        new(@"\]\(#(?<anchor>[a-z0-9\-]+)\)", RegexOptions.Compiled);

    [Fact]
    public void EveryClaudeSectionHasAnIndexRowInAgentsMd()
    {
        var sections = MarkdownGuide.AtRepositoryRoot("CLAUDE.md").HeadingAnchors();

        var unindexed = sections.Keys.Except(IndexedAnchors()).OrderBy(a => a).ToList();

        Assert.True(
            unindexed.Count == 0,
            "CLAUDE.md sections with no row in the AGENTS.md index table: "
                + string.Join(", ", unindexed.Select(a => $"\"{sections[a]}\" (#{a})")));
    }

    [Fact]
    public void EveryIndexRowPointsAtASectionThatStillExists()
    {
        var sections = MarkdownGuide.AtRepositoryRoot("CLAUDE.md").HeadingAnchors();

        var orphaned = IndexedAnchors().Except(sections.Keys).OrderBy(a => a).ToList();

        Assert.True(
            orphaned.Count == 0,
            "AGENTS.md index rows pointing at CLAUDE.md sections that no longer exist: "
                + string.Join(", ", orphaned.Select(a => "#" + a)));
    }

    [Fact]
    public void EveryCrossLinkFromAgentsMdResolves()
    {
        var sections = MarkdownGuide.AtRepositoryRoot("CLAUDE.md").HeadingAnchors();
        var agents = MarkdownGuide.AtRepositoryRoot("AGENTS.md");

        var dead = AnchorsLinkedBy(ClaudeAnchorLink, agents.Text).Except(sections.Keys).ToList();

        Assert.True(
            dead.Count == 0,
            "AGENTS.md links to CLAUDE.md headings that do not exist: "
                + string.Join(", ", dead.Select(a => "#" + a)));
    }

    [Fact]
    public void EveryInternalLinkInClaudeMdResolves()
    {
        var claude = MarkdownGuide.AtRepositoryRoot("CLAUDE.md");

        var dead = AnchorsLinkedBy(SelfAnchorLink, claude.Text)
            .Except(claude.HeadingAnchors().Keys)
            .ToList();

        Assert.True(
            dead.Count == 0,
            "CLAUDE.md links to its own headings that do not exist: "
                + string.Join(", ", dead.Select(a => "#" + a)));
    }

    /// <summary>
    /// The anchors the AGENTS.md index table claims to cover. A row either links its section
    /// directly, or defers to a rule reproduced in full further down the page with
    /// "&lt;emoji&gt; below". The reproduced section carries the link instead, so the deferral is
    /// resolved through that link rather than taken on trust.
    /// </summary>
    private static IReadOnlyList<string> IndexedAnchors()
    {
        var agents = MarkdownGuide.AtRepositoryRoot("AGENTS.md");
        var reproduced = ReproducedSections(agents);
        var claimed = new List<string>();

        foreach (var (rule, where) in IndexTableRows(agents))
        {
            var direct = ClaudeAnchorLink.Match(where);
            if (direct.Success)
            {
                claimed.Add(direct.Groups["anchor"].Value);
                continue;
            }

            var marker = where.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            Assert.True(
                marker is not null && reproduced.ContainsKey(marker),
                $"AGENTS.md index row \"{rule}\" points at neither a CLAUDE.md anchor nor a rule "
                    + $"reproduced on the page; its \"Where\" cell reads \"{where}\".");

            claimed.Add(reproduced[marker!]);
        }

        return claimed;
    }

    /// <summary>
    /// Maps the emoji that opens each reproduced AGENTS.md section to the first CLAUDE.md anchor
    /// that section links, which is the full-detail pointer closing it.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReproducedSections(MarkdownGuide agents)
    {
        var byMarker = new Dictionary<string, string>(StringComparer.Ordinal);
        string? marker = null;
        var body = new StringBuilder();

        void Close()
        {
            var link = ClaudeAnchorLink.Match(body.ToString());
            if (marker is not null && link.Success)
            {
                byMarker[marker] = link.Groups["anchor"].Value;
            }

            marker = null;

            // Cleared for every section, reproduced or not. Leaving the index table's own links in
            // the buffer would hand them to whichever reproduced section came next.
            body.Clear();
        }

        foreach (var line in agents.Lines(skipFencedBlocks: true))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();

                var first = line[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

                // A reproduced rule opens with the same emoji its index row defers to. A plain
                // heading such as "## Mandatory rules" starts with an ordinary word instead.
                if (!first.Any(char.IsLetterOrDigit))
                {
                    marker = first;
                }

                continue;
            }

            body.AppendLine(line);
        }

        Close();
        return byMarker;
    }

    private static IEnumerable<(string Rule, string Where)> IndexTableRows(MarkdownGuide agents)
    {
        var inTable = false;

        foreach (var line in agents.Lines(skipFencedBlocks: true))
        {
            if (!line.StartsWith('|'))
            {
                if (inTable)
                {
                    yield break;
                }

                continue;
            }

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 2)
            {
                continue;
            }

            // The header row and its "| --- |" separator carry no rule.
            if (cells[0] is "Rule" || cells[0].All(c => c is '-' or ':'))
            {
                inTable = true;
                continue;
            }

            if (inTable)
            {
                yield return (cells[0], cells[1]);
            }
        }
    }

    private static IReadOnlyList<string> AnchorsLinkedBy(Regex pattern, string markdown) =>
        pattern
            .Matches(markdown)
            .Select(m => m.Groups["anchor"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
}
