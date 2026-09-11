using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The search above a list of items. It changes what is shown and nothing else, so the rule worth
/// holding it to is that a tick it hides is counted and can be reported.
/// </summary>
public class ItemFilterTests
{
    private static DeleteDirectoryStep Build(string project, string group) =>
        new($@"C:\Users\testuser\src\{project}\node_modules", "Installed npm packages")
        {
            Facets = [new ItemFacet("Project", project)],
            Group = group,
        };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingTypedShowsEveryItem(string? query)
    {
        var filter = new ItemFilter(query);

        Assert.True(filter.IsEmpty);
        Assert.True(filter.Matches(Build("billing-api", "Archive 2024")));
    }

    /// <summary>
    /// "API" is only in the project and "2024" only in the heading, so neither the path nor any one
    /// field holds both words. Every word must be found, wherever it is.
    /// </summary>
    [Fact]
    public void EveryWordHasToAppearSomewhereInTheItemRegardlessOfCase()
    {
        var item = Build("billing-api", "Archive 2024");

        Assert.True(new ItemFilter("API 2024").Matches(item));
        Assert.True(new ItemFilter("2024   api").Matches(item));
        Assert.False(new ItemFilter("api 2025").Matches(item));
    }

    [Fact]
    public void AFacetsValueIsSearchedAndItsLabelIsNot()
    {
        var item = new DeleteDirectoryStep(@"C:\Users\testuser\src\x\node_modules", "Installed npm packages")
        {
            Facets = [new ItemFacet("Project", "ledger")],
        };

        Assert.True(new ItemFilter("ledger").Matches(item));
        Assert.False(new ItemFilter("project").Matches(item));
    }

    /// <summary>
    /// A search that hides a ticked item leaves it in the run. The count is what the list reports, so
    /// it counts the ticked items hidden and nothing else: not a hidden item left clear, and not a
    /// ticked item still on screen.
    /// </summary>
    [Fact]
    public void CountsOnlyTheTickedItemsASearchHides()
    {
        var shown = Build("billing-api", "Work");
        var hiddenTicked = Build("website", "Work");
        var hiddenClear = Build("docs", "Work");

        (CleanupStep, bool)[] items = [(shown, true), (hiddenTicked, true), (hiddenClear, false)];

        Assert.Equal(1, new ItemFilter("billing").HiddenSelected(items));
        Assert.Equal(0, new ItemFilter(null).HiddenSelected(items));
    }

    [Fact]
    public void ShowingKeepsTheOrderAndDropsAGroupLeftWithNothing()
    {
        var api = Build("billing-api", "Work");
        var web = Build("billing-web", "Work");
        var docs = Build("docs", "Personal");

        ItemGroup<CleanupStep>[] groups =
        [
            new("Work", [web, api]),
            new("Personal", [docs]),
        ];

        var shown = new ItemFilter("billing").Showing(groups, step => step);

        var only = Assert.Single(shown);
        Assert.Equal("Work", only.Name);
        Assert.Equal([web, api], only.Items);
    }
}
