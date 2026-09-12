using System.Globalization;

namespace Deguffer.Core.Tests;

/// <summary>
/// CLAUDE.md loads into every agent session, so its size is a cost on every task. It grew to about
/// 39,000 characters one reasonable addition at a time, each rule arriving with its recipe, its
/// incident and its rationale, until detail for one kind of change crowded out the rules for all of
/// them. These budgets stop that growth returning: a new rule has to fit, which means shortening or
/// moving something rather than appending. Raising a budget is the maintainer's decision, not a way
/// to make a change pass.
///
/// AGENTS.md once reproduced some rules and indexed the rest, which took a parity test to hold the
/// copies together. With no copy there is nothing to drift, so these tests guard only against a
/// copy returning.
/// </summary>
public sealed class AgentGuideBudgetTests
{
    private const int ClaudeFileBudget = 10_000;
    private const int ClaudeSectionBudget = 1_200;
    private const int AgentsFileBudget = 600;

    private const string HowToFix =
        "Move detail that applies to one kind of change into a memory note, and name the note from a "
        + "short rule. Do not raise the budget without asking the maintainer.";

    [Fact]
    public void ClaudeMdStaysWithinItsBudget()
    {
        var length = MarkdownGuide.AtRepositoryRoot("CLAUDE.md").Text.Length;

        Assert.True(
            length <= ClaudeFileBudget,
            $"CLAUDE.md is {length} characters, over its {ClaudeFileBudget} budget. {HowToFix}");
    }

    [Fact]
    public void EveryClaudeMdSectionStaysWithinItsBudget()
    {
        var sections = MarkdownGuide.AtRepositoryRoot("CLAUDE.md").Sections();

        // A heading form the sweep failed to recognise would pass every section without checking one.
        Assert.True(sections.Count > 5, $"Found only {sections.Count} sections in CLAUDE.md.");

        var over = sections
            .Where(s => s.Length > ClaudeSectionBudget)
            .Select(s => $"\"{s.Heading}\" ({s.Length})")
            .ToList();

        Assert.True(
            over.Count == 0,
            $"CLAUDE.md sections over the {ClaudeSectionBudget}-character budget: "
                + $"{string.Join(", ", over)}. {HowToFix}");
    }

    [Fact]
    public void ClaudeMdStatesTheBudgetsTheseTestsEnforce()
    {
        // An agent reads the numbers in CLAUDE.md, not here, so the two must agree.
        var claude = MarkdownGuide.AtRepositoryRoot("CLAUDE.md");

        foreach (var budget in new[] { ClaudeFileBudget, ClaudeSectionBudget, AgentsFileBudget })
        {
            var written = budget.ToString("N0", CultureInfo.InvariantCulture);

            Assert.True(
                claude.Text.Contains(written, StringComparison.Ordinal),
                $"CLAUDE.md does not state the {written}-character budget these tests enforce.");
        }
    }

    [Fact]
    public void AgentsMdIsOnlyAPointerToClaudeMd()
    {
        var agents = MarkdownGuide.AtRepositoryRoot("AGENTS.md");

        Assert.Contains("](CLAUDE.md)", agents.Text, StringComparison.Ordinal);

        Assert.True(
            agents.HeadingAnchors().Count == 0,
            "AGENTS.md has sections of its own. It is a pointer: put the rule in CLAUDE.md instead.");

        Assert.True(
            agents.Text.Length <= AgentsFileBudget,
            $"AGENTS.md is {agents.Text.Length} characters, over its {AgentsFileBudget} budget. It is "
                + "a pointer: put the rule in CLAUDE.md instead.");
    }
}
