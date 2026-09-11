using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a plan's items are gathered under headings and ordered. §7 sorts by size, and a heading is
/// compared the way the folder names most headings are.
/// </summary>
public class ItemGroupsTests
{
    private static DeleteDirectoryStep Item(string name, long bytes, string? group) =>
        new($@"C:\Users\testuser\src\{name}\node_modules", "Installed npm packages")
        {
            Estimated = ScanSize.FromLengths(bytes),
            Group = group,
        };

    [Fact]
    public void PutsTheLargestGroupFirstAndTheLargestItemFirstWithinIt()
    {
        var smallWork = Item("a", 10, "Work");
        var largeWork = Item("b", 30, "Work");
        var personal = Item("c", 50, "Personal");

        var groups = ItemGroups.Of<CleanupStep>([smallWork, largeWork, personal], step => step);

        Assert.Equal(["Personal", "Work"], groups.Select(g => g.Name));
        Assert.Equal([largeWork, smallWork], groups[1].Items);
    }

    /// <summary>
    /// Two items whose heading is one folder spelt two ways are one group, shown as first spelt.
    /// Splitting them would put half of one folder's projects under a heading of their own.
    /// </summary>
    [Fact]
    public void HeadingsThatDifferOnlyInCaseAreOneGroup()
    {
        var first = Item("a", 10, @"C:\Users\testuser\src");
        var second = Item("b", 20, @"c:\users\TESTUSER\SRC");

        var group = Assert.Single(ItemGroups.Of<CleanupStep>([first, second], step => step));

        Assert.Equal(@"C:\Users\testuser\src", group.Name);
        Assert.Equal(2, group.Items.Count);
    }

    [Fact]
    public void ItemsWithNoHeadingAreOneUnnamedGroupPlacedBySize()
    {
        var loose = Item("a", 100, null);
        var alsoLoose = Item("b", 5, null);
        var grouped = Item("c", 50, "Work");

        // The named group is met first, so only its size can put the unnamed group ahead of it.
        var groups = ItemGroups.Of<CleanupStep>([grouped, alsoLoose, loose], step => step);

        Assert.Equal([null, "Work"], groups.Select(g => g.Name));
        Assert.Equal([loose, alsoLoose], groups[0].Items);
    }

    [Fact]
    public void TiesKeepTheProvidersOrder()
    {
        var first = Item("a", 10, null);
        var second = Item("b", 10, null);
        var third = Item("c", 10, null);

        var group = Assert.Single(ItemGroups.Of<CleanupStep>([first, second, third], step => step));

        Assert.Equal([first, second, third], group.Items);
    }
}
