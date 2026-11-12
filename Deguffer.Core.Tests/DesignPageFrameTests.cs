namespace Deguffer.Core.Tests;

/// <summary>
/// Every page opens the same way, and its structure is one a screen reader can move through. A page
/// the rail opens names itself once with the shared page title, so the reader always knows where
/// they are, even with the rail collapsed to icons. The title and section heading styles carry their
/// heading levels, so Narrator's heading navigation finds the page's parts. A <c>Pivot</c> is a phone
/// pattern whose headers outweigh the page's own title; tabs between lists are a <c>SelectorBar</c>.
///
/// <para>WCAG 2.2 1.3.1, 2.4.2 and 2.4.6.</para>
/// </summary>
public sealed class DesignPageFrameTests
{
    private static readonly Dictionary<string, int> TitleBaseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/ExplorePage.xaml"] = 1,
        ["Deguffer.App/Views/HealthPage.xaml"] = 1,
        ["Deguffer.App/Views/InstalledAppsPage.xaml"] = 1,
        ["Deguffer.App/Views/MemoryPage.xaml"] = 1,
        ["Deguffer.App/Views/StartupPage.xaml"] = 1,
    };

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
    public void EveryPageNamesItselfOnceWithThePageTitle()
    {
        var found = ShellMarkup.Pages.SelectMany(page =>
        {
            var titles = page.Elements.Count(element => element.Attribute("Style")?.Value is { } style
                && style.Contains("PageTitle", StringComparison.Ordinal));

            return Enumerable.Repeat(page.At(page.Document.Root!, $"the page has {titles} PageTitle headings, not one"), Math.Abs(titles - 1));
        });

        DesignBaseline.Hold("every page opens with exactly one PageTitle", found, TitleBaseline);
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

        DesignBaseline.Hold("PageTitle is a level-one heading and SectionHeading a level-two heading", found, HeadingBaseline);
    }

    [Fact]
    public void ListsAreSwitchedWithASelectorBarNotAPivot()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Name.LocalName == "Pivot")
            .Select(pivot => file.At(pivot, "a Pivot, where the shell uses a SelectorBar")));

        DesignBaseline.Hold("no Pivot: switch between lists with a SelectorBar", found, PivotBaseline);
    }
}
