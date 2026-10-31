using System.Globalization;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Explore map's control is the host its parts meet in, and it grew to over 3,000 lines one
/// reasonable addition at a time before those parts were given types of their own. Its budget stops
/// that growth returning: something new that would take the file past it is a part of its own, or
/// moves one out first. Raising the budget is the maintainer's decision, not a way to make a change
/// pass.
///
/// <para>Read from the source, because the App's controls cannot be loaded in a test process, and a
/// file's length is a fact of its text.</para>
/// </summary>
public sealed class ExploreMapBudgetTests
{
    private const int LineBudget = 2_300;

    private static readonly string Host =
        Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.App", "Controls", "ExploreMap.cs");

    [Fact]
    public void TheMapsHostStaysWithinItsBudget()
    {
        var lines = File.ReadLines(Host).Count();

        Assert.True(
            lines <= LineBudget,
            $"ExploreMap.cs is {lines} lines, over its {LineBudget} budget. Move a part of what it does into a "
                + "type of its own rather than raising the budget, which is the maintainer's decision.");
    }

    [Fact]
    public void TheMapsHostStatesTheBudgetThisTestEnforces()
    {
        // A reader of the class learns the budget from its doc, not from here, so the two must agree.
        var written = LineBudget.ToString("N0", CultureInfo.InvariantCulture) + " lines";

        Assert.Contains(written, File.ReadAllText(Host), StringComparison.Ordinal);
    }
}
