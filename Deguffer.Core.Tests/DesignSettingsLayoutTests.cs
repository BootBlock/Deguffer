using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Settings page is grouped by what people come to change, with the settings that decide what may
/// be deleted in view and the scanning tuning folded away, as Windows Settings keeps common settings
/// in view and folds rarely changed ones into an expander whose header carries the summary.
///
/// <para>The scanning values change how fast a scan reads the disk, never what it finds. Shown with
/// the prominence of the safety settings, they buried them, and five values that apply to one kind of
/// drive read as settings for every drive.</para>
/// </summary>
public sealed class DesignSettingsLayoutTests
{
    private const string Page = "Deguffer.App/Views/SettingsPage.xaml";
    private const string Section = "Deguffer.App/Views/ScanSettingsSection.xaml";

    /// <summary>The values the kind-of-drive picker governs, each bound to its own property.</summary>
    private static readonly string[] KindValues =
        ["WalkThreads", "ListingBufferKiB", "TableReadKiB", "TableReadsInFlight", "TableListReadsInFlight"];

    [Fact]
    public void TheGroupsFollowWhatPeopleComeToChange() =>
        Assert.Equal(
            ["Appearance", "Safety", "Where Deguffer looks", "History and privacy", "Advanced"],
            Groups().Select(group => group.Heading));

    /// <summary>The safety group holds the five guards on what a clean removes, and nothing that only changes speed.</summary>
    [Fact]
    public void SafetyHoldsTheFiveGuards() =>
        Assert.Equal(
            [
                "Confirm before cleaning",
                "Type a name to delete user data",
                "Leave recently changed files alone",
                "Only offer temporary files older than",
                "Keep File History versions for",
            ],
            RowTitles(Groups().Single(group => group.Heading == "Safety").Members));

    /// <summary>
    /// How Recycle Bins are emptied trades speed against how promptly Windows notices, and removes the
    /// same files either way, so it is advanced, beside the scanning section.
    /// </summary>
    [Fact]
    public void AdvancedHoldsTheRecycleBinRouteAndTheScanningSection()
    {
        var advanced = Groups().Single(group => group.Heading == "Advanced").Members;

        Assert.Equal(["Empty Recycle Bins without Windows"], RowTitles(advanced));
        Assert.Single(advanced, element => element.Name.LocalName == "ScanSettingsSection");
    }

    [Fact]
    public void TheScanningExpanderIsCollapsedOnFirstLoad()
    {
        var expander = ScanningExpander();

        Assert.True(
            expander.Attribute("IsExpanded") is null or { Value: "False" },
            $"The scanning expander must start closed, but sets IsExpanded=\"{expander.Attribute("IsExpanded")?.Value}\".");
    }

    /// <summary>
    /// Every control bound to the scanning section, its failure notice and the drives' readout
    /// included, is in the expander's content, and nothing outside the section binds to it, so no
    /// scanning value is left in view beside the safety settings. The header stays in view while the
    /// expander is closed, so it may carry only the summary, and the expander itself only names it.
    /// </summary>
    [Fact]
    public void EveryScanningRowIsInsideTheExpander()
    {
        var expander = ScanningExpander();
        var header = expander.Elements().Single(element => element.Name.LocalName == "Expander.Header");
        var bound = ShellMarkup.Files
            .SelectMany(file => file.Elements.Where(BindsScanning).Select(element => (file, element)))
            .Where(found => found.element != expander && !IsTheSummary(found.element))
            .ToList();

        Assert.Contains(bound, found => found.element.Attribute("Value")?.Value.Contains("Scanning.WalkThreads", StringComparison.Ordinal) == true);
        Assert.All(bound, found => Assert.True(
            found.file.Name == Section && found.element.Ancestors().Contains(expander) && !found.element.Ancestors().Contains(header),
            $"{found.file.Name}:{ShellMarkup.LineOf(found.element)} binds the scanning section outside its expander's content."));
    }

    private static bool IsTheSummary(XElement element) =>
        element.Name.LocalName == "TextBlock"
        && element.Attributes().Count(attribute => attribute.Value.Contains("x:Bind Scanning.", StringComparison.Ordinal)) == 1
        && element.Attribute("Text")?.Value == "{x:Bind Scanning.Summary, Mode=OneWay}";

    /// <summary>The folded header says what holds, so a scan tuned on one kind of drive is never hidden by closing it.</summary>
    [Fact]
    public void TheExpandersHeaderCarriesTheSummary()
    {
        var header = ScanningExpander().Elements().Single(element => element.Name.LocalName == "Expander.Header");

        Assert.Contains(header.Descendants(), element => element.Attribute("Text")?.Value == "{x:Bind Scanning.Summary, Mode=OneWay}");
    }

    /// <summary>
    /// The kind-of-drive picker heads a card of its own, and the five values it governs and the drives'
    /// readout sit in it, indented under the picker, so none reads as a setting for every drive.
    /// </summary>
    [Fact]
    public void TheValuesOfAKindOfDriveSitUnderItsPicker()
    {
        var section = File(Section);
        var picker = section.Elements.Single(element => element.Attribute("SelectedIndex")?.Value.Contains("Scanning.KindIndex", StringComparison.Ordinal) == true);
        var card = picker.Ancestors().First(element => element.Name.LocalName == "Border");
        var indented = card.Descendants().Single(element => element.Attribute("Margin")?.Value == "{StaticResource NestedRowIndent}");

        Assert.Equal("{StaticResource CardStyle}", card.Attribute("Style")?.Value);
        Assert.DoesNotContain(indented, picker.Ancestors());
        Assert.All(KindValues, value => Assert.Contains(
            indented.Descendants(),
            element => element.Attribute("Value")?.Value == $"{{x:Bind Scanning.{value}, Mode=TwoWay}}"));
        Assert.Contains(indented.Descendants(), element => element.Attribute("ItemsSource")?.Value == "{x:Bind Scanning.Drives, Mode=OneWay}");
    }

    private static MarkupFile File(string name) => ShellMarkup.Files.Single(file => file.Name == name);

    private static XElement ScanningExpander() =>
        File(Section).Elements.Single(element => element.Name.LocalName == "Expander");

    private static bool BindsScanning(XElement element) =>
        element.Attributes().Any(attribute => attribute.Value.Contains("x:Bind Scanning.", StringComparison.Ordinal));

    private static IEnumerable<string> RowTitles(IEnumerable<XElement> members) =>
        members.Where(element => element.Name.LocalName == "SettingsRow").Select(row => row.Attribute("Title")!.Value);

    /// <summary>
    /// The page's groups in order: each group heading in the page's one stack, and the stack's
    /// children after it up to the next heading.
    /// </summary>
    private static List<(string Heading, List<XElement> Members)> Groups()
    {
        var headings = File(Page).Elements
            .Where(element => element.Name.LocalName == "TextBlock" && element.Attribute("Style")?.Value == "{StaticResource GroupHeading}")
            .ToList();
        var stack = headings[0].Parent!;

        Assert.All(headings, heading => Assert.Same(stack, heading.Parent));

        var groups = new List<(string Heading, List<XElement> Members)>();

        foreach (var child in stack.Elements())
        {
            if (headings.Contains(child))
            {
                groups.Add((child.Attribute("Text")!.Value, []));
            }
            else if (groups.Count > 0)
            {
                groups[^1].Members.Add(child);
            }
        }

        return groups;
    }
}
