using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// The navigation rail is the first thing a reader scans, so its order says what belongs together.
/// The pages about the disk come first, Storage leading because it is where the window opens, and
/// Health with them because the disk pages name a drive of concern from what it read. The pages
/// about programs follow a separator, from what is installed through what starts at sign-in to what
/// runs now. Settings and About sit in the footer.
///
/// <para>Each rail item is routed to its page by the item itself, so every item is named for the
/// window's table and none carries a <c>Tag</c>: a string there is a second name that can drift from
/// the first, and a page lookup on it needs a fallback that opens the wrong page in silence.</para>
/// </summary>
public sealed class DesignRailTests
{
    private const string Separator = "(separator)";

    private static readonly string[] MenuOrder =
    [
        "Storage", "Explore", "Duplicates", "Health",
        Separator,
        "Installed apps", "Startup", "Memory",
    ];

    private static readonly string[] FooterOrder = ["Settings", "About"];

    [Fact]
    public void TheRailGroupsTheDiskPagesBeforeTheProgramPages()
    {
        Assert.Equal(MenuOrder, Entries("NavigationView.MenuItems"));
        Assert.Equal(FooterOrder, Entries("NavigationView.FooterMenuItems"));
    }

    [Fact]
    public void EveryRailItemIsNamedAndCarriesNoTag()
    {
        var items = Rail().Descendants().Where(element => element.Name.LocalName == "NavigationViewItem").ToList();

        Assert.Equal(MenuOrder.Length - 1 + FooterOrder.Length, items.Count);
        Assert.All(items, item =>
        {
            Assert.NotNull(item.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "Name"));
            Assert.Null(item.Attribute("Tag"));
        });
    }

    private static IEnumerable<string> Entries(string collection) =>
        Rail().Elements()
            .Single(element => element.Name.LocalName == collection)
            .Elements()
            .Select(entry => entry.Name.LocalName switch
            {
                "NavigationViewItem" => entry.Attribute("Content")?.Value ?? "(unnamed)",
                "NavigationViewItemSeparator" => Separator,
                var other => other,
            });

    private static XElement Rail()
    {
        var window = Assert.Single(ShellMarkup.Files, file => file.Name == "Deguffer.App/MainWindow.xaml");

        return Assert.Single(window.Elements, element => element.Name.LocalName == "NavigationView");
    }
}
