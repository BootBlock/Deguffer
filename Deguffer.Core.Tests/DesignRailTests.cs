using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// The navigation rail is the first thing a reader scans, so its order says what belongs together.
/// The pages about the drives come first under their header, Storage leading because it is where
/// the window opens, and Health with them because the disk pages name a drive of concern from what
/// it read. The pages about programs follow a separator and their own header, from what is installed
/// through what starts at sign-in to what runs now. The separator parts the groups too, because a
/// header collapses to nothing with the rail closed. Settings and About sit in the footer.
///
/// <para>A one-word label cannot say what its page is for, so every item states its question as
/// help text, for a screen reader, and as a tooltip that leads with the label, because the tooltip
/// is the only name a collapsed item shows. The test holds the tooltip to the label and the help
/// text, so the two cannot drift apart.</para>
///
/// <para>Each rail item is routed to its page by the item itself, so every item is named for the
/// window's table and none carries a <c>Tag</c>: a string there is a second name that can drift from
/// the first, and a page lookup on it needs a fallback that opens the wrong page in silence.</para>
/// </summary>
public sealed class DesignRailTests
{
    private const string Separator = "(separator)";

    private const string HeaderMark = "(header) ";

    private static readonly string[] MenuOrder =
    [
        HeaderMark + "Drives",
        "Storage", "Explore", "Duplicates", "Health",
        Separator,
        HeaderMark + "Programs",
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
        var items = Items();

        Assert.Equal(MenuOrder.Count(entry => entry != Separator && !entry.StartsWith(HeaderMark, StringComparison.Ordinal)) + FooterOrder.Length, items.Count);
        Assert.All(items, item =>
        {
            Assert.NotNull(item.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "Name"));
            Assert.Null(item.Attribute("Tag"));
        });
    }

    [Fact]
    public void EveryRailItemStatesItsQuestionAsHelpTextAndATooltipLedByItsLabel()
    {
        Assert.All(Items(), item =>
        {
            var label = item.Attribute("Content")?.Value ?? "";
            var help = ShellMarkup.Literal(item, "AutomationProperties.HelpText");
            var tooltip = ShellMarkup.Literal(item, "ToolTipService.ToolTip");

            Assert.NotNull(help);
            Assert.False(string.IsNullOrWhiteSpace(help), $"{label} has blank help text.");
            Assert.NotEqual(label, help, StringComparer.OrdinalIgnoreCase);
            Assert.Equal($"{label}: {char.ToLowerInvariant(help[0])}{help[1..]}", tooltip);
        });
    }

    private static List<XElement> Items() =>
        Rail().Descendants().Where(element => element.Name.LocalName == "NavigationViewItem").ToList();

    private static IEnumerable<string> Entries(string collection) =>
        Rail().Elements()
            .Single(element => element.Name.LocalName == collection)
            .Elements()
            .Select(entry => entry.Name.LocalName switch
            {
                "NavigationViewItem" => entry.Attribute("Content")?.Value ?? "(unnamed)",
                "NavigationViewItemSeparator" => Separator,
                "NavigationViewItemHeader" => HeaderMark + (entry.Attribute("Content")?.Value ?? "(unnamed)"),
                var other => other,
            });

    private static XElement Rail()
    {
        var window = Assert.Single(ShellMarkup.Files, file => file.Name == "Deguffer.App/MainWindow.xaml");

        return Assert.Single(window.Elements, element => element.Name.LocalName == "NavigationView");
    }
}
