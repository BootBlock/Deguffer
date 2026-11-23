using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every page opens the same way, and its structure is one a screen reader can move through. A page
/// the rail opens is one panel padded by <c>PagePadding</c> whose first row is the shared
/// <c>PageHeader</c>: the page's name on the left in the one title style, and what governs the page
/// as a whole on the right. So the title, the header's buttons and the content's left edge stay where
/// they were as the reader moves between pages, and the reader always knows where they are, even with
/// the rail collapsed to icons. The title and section heading styles carry their heading levels, so
/// Narrator's heading navigation finds the page's parts. A <c>Pivot</c> is a phone pattern whose
/// headers outweigh the page's own title; tabs between lists are a <c>SelectorBar</c>.
///
/// <para>WCAG 2.2 1.3.1, 2.4.2 and 2.4.6; Nielsen's visibility of system status, and consistency and
/// standards.</para>
/// </summary>
public sealed class DesignPageFrameTests
{
    private const string Header = "PageHeader";

    private const string HeaderMarkup = "Deguffer.App/Controls/PageHeader.xaml";

    /// <summary>The one page whose destination is not its file's name: Storage is drawn by the cleaning page.</summary>
    private static readonly Dictionary<string, string> DestinationByPage = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/CleanPage.xaml"] = "Storage",
    };

    private static readonly Dictionary<string, int> NoExceptions = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, int> HeadingBaseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/App.xaml"] = 2,
    };

    private static readonly Dictionary<string, int> PivotBaseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/InstalledAppsPage.xaml"] = 1,
        ["Deguffer.App/Views/ProviderInfoView.xaml"] = 1,
    };

    [Fact]
    public void EveryPageOpensWithThePageHeader()
    {
        var found = ShellMarkup.Pages.SelectMany(page =>
        {
            var headers = page.Elements.Count(element => element.Name.LocalName == Header);
            var first = Root(page) is { Name.LocalName: "Grid" } root ? Children(root).FirstOrDefault() : null;

            var problems = new List<RuleViolation>();

            if (first is not { Name.LocalName: Header } || Row(first) != 0)
            {
                problems.Add(page.At(first ?? page.Document.Root!, "the page's root is not a Grid whose first child is the PageHeader, in row 0"));
            }

            if (headers != 1)
            {
                problems.Add(page.At(page.Document.Root!, $"the page has {headers} PageHeaders, not one"));
            }

            return problems;
        });

        RuleBaseline.Hold("every page's root is a Grid that opens with one PageHeader", found, NoExceptions);
    }

    [Fact]
    public void EveryPageRootTakesThePagePadding()
    {
        var found = ShellMarkup.Pages
            .Where(page => Root(page)?.Attribute("Padding")?.Value != "{StaticResource PagePadding}")
            .Select(page => page.At(Root(page) ?? page.Document.Root!, "the page's root does not take its Padding from PagePadding"));

        RuleBaseline.Hold("every page's root is padded by PagePadding", found, NoExceptions);
    }

    /// <summary>
    /// The header names the page from its destination, which is also what the rail and the window
    /// title read, so a header copied from another page would name the wrong page in all three.
    /// </summary>
    [Fact]
    public void EveryPageHeaderNamesItsOwnDestination()
    {
        var found = ShellMarkup.Pages.SelectMany(page =>
        {
            var expected = DestinationByPage.GetValueOrDefault(page.Name)
                ?? Path.GetFileNameWithoutExtension(page.Name)[..^"Page".Length];

            return page.Elements
                .Where(element => element.Name.LocalName == Header && ShellMarkup.Literal(element, "Destination") != expected)
                .Select(element => page.At(element, $"the PageHeader names {ShellMarkup.Literal(element, "Destination") ?? "no destination"}, not {expected}"));
        });

        RuleBaseline.Hold("every PageHeader names its own page's destination", found, NoExceptions);
    }

    /// <summary>The page title is drawn by the header alone, so no page can title itself another way.</summary>
    [Fact]
    public void OnlyThePageHeaderUsesThePageTitle()
    {
        var found = ShellMarkup.Files
            .Where(file => file.Name != HeaderMarkup && !file.IsApplicationResources)
            .SelectMany(file => file.Elements
                .Where(element => element.Attribute("Style")?.Value is { } style && style.Contains("PageTitle", StringComparison.Ordinal))
                .Select(element => file.At(element, "PageTitle outside the PageHeader")));

        RuleBaseline.Hold("a page is titled by its PageHeader, never by PageTitle on its own", found, NoExceptions);
    }

    /// <summary>
    /// The account notice stands above every page, so it is inset as the pages are; the About page's
    /// starfield bleeds out of exactly the padding the pages take.
    /// </summary>
    [Fact]
    public void TheShellInsetsFollowThePagePadding()
    {
        var app = Assert.Single(ShellMarkup.Files, file => file.IsApplicationResources);
        var window = Assert.Single(ShellMarkup.Files, file => file.Name == "Deguffer.App/MainWindow.xaml");

        var padding = Thickness(app, "PagePadding");
        var notice = Assert.Single(window.Elements, element => element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "AccountNotice");

        Assert.Equal("{StaticResource PageNoticeMargin}", notice.Attribute("Margin")?.Value);
        var noticeMargin = Thickness(app, "PageNoticeMargin");
        Assert.Equal(new[] { padding[0], padding[2] }, new[] { noticeMargin[0], noticeMargin[2] });
        Assert.Equal(new[] { -padding[0], 0, -padding[2], -padding[3] }, Thickness(app, "PageBleed"));
    }

    [Fact]
    public void TheHeadingStylesCarryTheirHeadingLevels()
    {
        var app = Assert.Single(ShellMarkup.Files, file => file.IsApplicationResources);

        var found = new[] { ("PageTitle", "Level1"), ("SectionHeading", "Level2") }
            .SelectMany(heading => app.Elements
                .Where(element => element.Name.LocalName == "Style"
                    && element.Attributes().Any(attribute => attribute.Name.LocalName == "Key" && attribute.Value == heading.Item1))
                .Where(style => !style.Elements().Any(setter => ShellMarkup.SetterLiteral(setter, "AutomationProperties.HeadingLevel") == heading.Item2))
                .Select(style => app.At(style, $"{heading.Item1} does not set AutomationProperties.HeadingLevel to {heading.Item2}")));

        RuleBaseline.Hold("PageTitle is a level-one heading and SectionHeading a level-two heading", found, HeadingBaseline);
    }

    [Fact]
    public void ListsAreSwitchedWithASelectorBarNotAPivot()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Name.LocalName == "Pivot")
            .Select(pivot => file.At(pivot, "a Pivot, where the shell uses a SelectorBar")));

        RuleBaseline.Hold("no Pivot: switch between lists with a SelectorBar", found, PivotBaseline);
    }

    /// <summary>The panel the page is laid out in: the page's content, past its resources.</summary>
    private static XElement? Root(MarkupFile page) => Children(page.Document.Root!).FirstOrDefault();

    /// <summary>An element's children, leaving out property elements such as <c>Page.Resources</c>.</summary>
    private static IEnumerable<XElement> Children(XElement element) =>
        element.Elements().Where(child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal));

    private static int Row(XElement element) =>
        element.Attribute("Grid.Row")?.Value is { } row ? int.Parse(row, System.Globalization.CultureInfo.InvariantCulture) : 0;

    private static double[] Thickness(MarkupFile app, string key) =>
        ShellMarkup.Lengths(Assert.Single(app.Elements, element => element.Name.LocalName == "Thickness"
            && element.Attributes().Any(attribute => attribute.Name.LocalName == "Key" && attribute.Value == key)).Value).ToArray();
}
