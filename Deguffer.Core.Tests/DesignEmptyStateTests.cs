using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every page with nothing to show yet says so through the one <c>EmptyState</c> control. A page
/// meets a first-time user in this state before any other, so it is where the app most needs to
/// look like one product (#930). The control takes each part's size from a named style, so the
/// pages share one layout and one size per part.
/// </summary>
public sealed partial class DesignEmptyStateTests
{
    private const string Control = "EmptyState";

    private const string ControlFile = "Deguffer.App/Controls/EmptyState.xaml";

    /// <summary>The pages that show a list or a picture in place of which they say why it is empty.</summary>
    public static TheoryData<string> PagesWithAnEmptyState =>
    [
        "Deguffer.App/Views/CleanPage.xaml",
        "Deguffer.App/Views/DuplicatesPage.xaml",
        "Deguffer.App/Views/ExplorePage.xaml",
        "Deguffer.App/Views/InstalledAppsPage.xaml",
        "Deguffer.App/Views/MemoryPage.xaml",
    ];

    [Theory]
    [MemberData(nameof(PagesWithAnEmptyState))]
    public void EachPagesEmptyStateIsTheSharedControl(string page)
    {
        var file = Assert.Single(ShellMarkup.Files, file => file.Name == page);

        var states = file.Elements.Where(element => element.Name.LocalName == Control).ToList();

        Assert.True(states.Count > 0, $"{page} shows no {Control}, so its empty state is drawn some other way.");
        Assert.All(states, state => Assert.False(
            string.IsNullOrWhiteSpace(state.Attribute("Title")?.Value),
            $"{page}:{ShellMarkup.LineOf(state)} draws an {Control} with no title."));
    }

    /// <summary>
    /// A banner shown in place of a list, centred where the rows would be, is the control and not a
    /// copy of it. A copy is known by its visibility, bound to a <c>HasNo…</c> or <c>…Empty…</c>
    /// property, and by its centring. A note that sits left-aligned under a list in Settings is a
    /// line of the list, not a banner, so it is not one.
    /// </summary>
    [Fact]
    public void EveryBannerInPlaceOfAListIsTheSharedControl()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Name.LocalName != Control && IsShownWhenEmpty(element) && IsCentred(element))
            .Select(element => file.At(element, $"<{element.Name.LocalName}> is drawn in place of an empty list; use controls:{Control}")));

        RuleBaseline.Hold("a banner in place of an empty list is an EmptyState", found, new Dictionary<string, int>());
    }

    /// <summary>
    /// The control's parts are drawn by the control alone. A page that took its styles to assemble a
    /// banner of its own would look the same today and drift the first time either changed.
    /// </summary>
    [Fact]
    public void OnlyTheControlDrawsAnEmptyStatesParts()
    {
        var found = ShellMarkup.Files
            .Where(file => file.Name != ControlFile)
            .SelectMany(file => file.Elements
                .SelectMany(element => element.Attributes())
                .Where(attribute => PartReference().IsMatch(attribute.Value))
                .Select(attribute => file.At(attribute, $"{attribute.Name.LocalName}=\"{attribute.Value}\" draws a part of an {Control} outside it")));

        RuleBaseline.Hold("only EmptyState draws its parts", found, new Dictionary<string, int>());
    }

    // A sweep that stopped finding the controls would pass the rules above with nothing to check,
    // so it must find at least one on each page listed above and in each of the two searches' lists.
    [Fact]
    public void TheSweepFindsTheEmptyStates()
    {
        var count = ShellMarkup.Files.Sum(file => file.Elements.Count(element => element.Name.LocalName == Control));

        Assert.True(count >= 7, $"Only {count} {Control} elements were found, so the rule is no longer reading the pages.");
    }

    private static bool IsShownWhenEmpty(XElement element) =>
        element.Attribute("Visibility")?.Value is { } visibility && EmptyFlag().IsMatch(visibility);

    private static bool IsCentred(XElement element) =>
        element.Attribute("HorizontalAlignment")?.Value == "Center";

    [GeneratedRegex(@"\{x:Bind\s+(?:[\w.]+\.)?(?:HasNo\w*|\w*Empty\w*)\b")]
    private static partial Regex EmptyFlag();

    [GeneratedRegex(@"\{(?:StaticResource|ThemeResource)\s+EmptyState(?:Glyph|Title|Message)\s*\}")]
    private static partial Regex PartReference();
}
