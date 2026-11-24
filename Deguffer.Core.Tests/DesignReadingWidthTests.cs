using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every page keeps its lists, forms and the controls round them to one reading width, and only what
/// fills the window by design runs past it: the page's header, the starfield behind About, a
/// scroller whose own column is narrower still, and a map. Past a list's natural width, a row's name
/// and its figures drift a screen apart, the eye loses the line between them (Gestalt proximity), and
/// the controls that go with a row end up a long way from it (Fitts' law). A picture is read as a
/// whole, so a wider window gives it more to show.
///
/// <para>The width is one <c>ReadingWidth</c> resource in <c>App.xaml</c>, applied by
/// <c>Controls/ReadingColumn</c> on each page's root, so a page added later is capped from its first
/// line and a child leaves the column only by saying it fills.</para>
/// </summary>
public sealed class DesignReadingWidthTests
{
    private const string Limit = "ReadingColumn.Limit";

    private const string Fills = "ReadingColumn.Fills";

    /// <summary>A map's host fills while its view draws a picture, and keeps to the column while it lists rows.</summary>
    private const string MapFills = "{x:Bind exploring:ExploreViewDrawing.DrawsPicture(ViewModel.SelectedView), Mode=OneWay}";

    private static readonly Dictionary<string, int> NoExceptions = new(StringComparer.Ordinal);

    [Fact]
    public void EveryPageIsLaidOutInTheReadingColumn()
    {
        var found = ShellMarkup.Pages.SelectMany(page =>
        {
            var root = Root(page);
            var problems = new List<RuleViolation>();

            if (Attribute(root, Limit) != "{StaticResource ReadingWidth}")
            {
                problems.Add(page.At(root, "the page's root does not take ReadingColumn.Limit from ReadingWidth"));
            }

            if (ShellMarkup.Sets(root, "ColumnDefinitions") || root.Elements().Any(child => child.Name.LocalName == "Grid.ColumnDefinitions"))
            {
                problems.Add(page.At(root, "the page's root declares its own columns, which are the reading column's"));
            }

            return problems;
        });

        RuleBaseline.Hold("every page's root is laid out in the reading column", found, NoExceptions);
    }

    /// <summary>
    /// A child leaves the reading column only through <c>Fills</c>, never by a column of its own, and
    /// only where it fills the window by design. A map's host fills only while its view draws a
    /// picture, so the list and the tree on the same card keep to the column.
    /// </summary>
    [Fact]
    public void OnlyWhatFillsTheWindowLeavesTheReadingColumn()
    {
        var found = ShellMarkup.Pages.SelectMany(page => Children(Root(page)).SelectMany(child =>
        {
            var problems = new List<RuleViolation>();
            var fills = Attribute(child, Fills) is "True" or MapFills;

            if (child.Attribute("Grid.Column") is not null || child.Attribute("Grid.ColumnSpan") is not null)
            {
                problems.Add(page.At(child, $"a {child.Name.LocalName} on the page's root places itself in a column, rather than filling through ReadingColumn.Fills"));
            }

            if (fills && !MayFill(child))
            {
                problems.Add(page.At(child, $"a {child.Name.LocalName} fills the window, where only the header, a starfield, a capped scroller or a map may"));
            }

            if (!fills && child.Name.LocalName == "PageHeader")
            {
                problems.Add(page.At(child, $"a {child.Name.LocalName} that should fill the window keeps to the reading column"));
            }

            if (HoldsMap(child) && Attribute(child, Fills) != MapFills)
            {
                problems.Add(page.At(child, "a map's host does not fill the window by whether its view draws a picture, so a list on it would run past the reading width"));
            }

            return problems;
        }));

        RuleBaseline.Hold("only what fills the window by design leaves the reading column", found, NoExceptions);
    }

    private static bool MayFill(XElement child) => child.Name.LocalName switch
    {
        "PageHeader" or "Starfield" => true,
        "ScrollViewer" => Children(child).FirstOrDefault()?.Attribute("MaxWidth")?.Value == "{StaticResource FormReadingWidth}",
        _ => HoldsMap(child),
    };

    private static bool HoldsMap(XElement child) => child.Descendants().Any(element => element.Name.LocalName == "ExploreMap");

    /// <summary>An attribute by its local name, which is how an attached property from another namespace is read.</summary>
    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static XElement Root(MarkupFile page) => Children(page.Document.Root!).First();

    /// <summary>An element's children, leaving out property elements such as <c>Page.Resources</c>.</summary>
    private static IEnumerable<XElement> Children(XElement element) =>
        element.Elements().Where(child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal));
}
