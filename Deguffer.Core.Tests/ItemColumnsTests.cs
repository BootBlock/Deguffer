using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The columns a list of items is read down. What can go wrong is a value landing under the wrong
/// heading, which reads as a fact about the item and is not one.
/// </summary>
public class ItemColumnsTests
{
    private static DeleteDirectoryStep Item(string project, params ItemFacet[] facets) =>
        new($@"C:\Users\testuser\src\{project}\node_modules", "Installed npm packages") { Facets = facets };

    [Fact]
    public void ColumnsAreEveryLabelInTheOrderItemsFirstNameThem()
    {
        CleanupStep[] steps =
        [
            Item("api", new ItemFacet("Project", "api")),
            Item("web", new ItemFacet("Project", "web"), new ItemFacet("Branch", "main")),
            Item("docs", new ItemFacet("Branch", "draft"), new ItemFacet("Version", "2")),
        ];

        Assert.Equal(["Project", "Branch", "Version"], ItemColumns.Of(steps).Labels);
    }

    /// <summary>
    /// The item that names only the second column must not have its value shown under the first. A
    /// list built positionally from each item's own facets would do exactly that.
    /// </summary>
    [Fact]
    public void EachValueLandsUnderItsOwnLabelAndAMissingOneIsEmpty()
    {
        var web = Item("web", new ItemFacet("Project", "web"), new ItemFacet("Branch", "main"));
        var docs = Item("docs", new ItemFacet("Branch", "draft"));

        var columns = ItemColumns.Of([web, docs]);

        Assert.Equal(["web", "main"], columns.ValuesOf(web));
        Assert.Equal(["", "draft"], columns.ValuesOf(docs));
    }

    [Fact]
    public void AStepThatIsNotAnItemHasAnEmptyValueInEveryColumn()
    {
        var command = new RunCommandStep("npm.cmd", "cache clean --force", "Clear the npm cache");
        var columns = ItemColumns.Of([command, Item("api", new ItemFacet("Project", "api"))]);

        Assert.Equal([""], columns.ValuesOf(command));
    }

    [Fact]
    public void APlanWhoseItemsCarryNoFacetsHasNoColumns()
    {
        var item = Item("api");
        var columns = ItemColumns.Of([item]);

        Assert.Empty(columns.Labels);
        Assert.Empty(columns.ValuesOf(item));
    }

    [Fact]
    public void TheFirstValueStandsWhereAnItemNamesALabelTwice()
    {
        var item = Item("api", new ItemFacet("Project", "api"), new ItemFacet("Project", "shadow"));

        Assert.Equal(["api"], ItemColumns.Of([item]).ValuesOf(item));
    }

    /// <summary>
    /// An item a tool's own command removes is listed as a deleted one is. A column read from
    /// deletions alone would leave LM Studio's runtimes with no version beside them.
    /// </summary>
    [Fact]
    public void AnItemRemovedByCommandHasItsColumnsToo()
    {
        var runtime = new RunCommandStep("lms.exe", "runtime remove --yes llama.cpp-win-x86_64-avx2@2.13.0", "Remove a runtime")
        {
            Removes = @"C:\Users\testuser\.lmstudio\extensions\backends\llama.cpp-win-x86_64-avx2-2.13.0",
            Facets = [new ItemFacet("Version", "2.13.0")],
        };

        var columns = ItemColumns.Of([runtime]);

        Assert.Equal(["Version"], columns.Labels);
        Assert.Equal(["2.13.0"], columns.ValuesOf(runtime));
    }
}
