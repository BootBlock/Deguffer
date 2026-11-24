using System.Globalization;
using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// Margins, padding and spacing are multiples of 4 effective pixels. Fluent lays every control out
/// on a 4 px grid, so a 10 or a 14 beside it puts edges and baselines a pixel or two out of line with
/// the controls around them, and the same gap comes out as 10 on one page and 16 on the next. A value
/// of 1 is a hairline, which the grid allows.
///
/// <para>The scale in <c>App.xaml</c> names each role once (label gap, control gap, region gap, card
/// and row padding), and the few values off the grid are optical corrections declared there by name,
/// each listed below with its reason, so an odd pixel is never written on a page.</para>
///
/// <para>Fluent's spacing guidance: every margin, padding and gap is a multiple of 4.</para>
/// </summary>
public sealed class DesignSpacingTests
{
    private static readonly string[] Properties = ["Margin", "Padding", "Spacing", "RowSpacing", "ColumnSpacing", "MinRowSpacing", "MinColumnSpacing"];

    private static readonly XName Key = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");

    /// <summary>
    /// The resources allowed off the grid, each an optical correction that sets what is inside an
    /// on-grid box against a glyph, a line of text or a baseline, or a value a re-templated control
    /// keeps from the SDK's own template so it draws as the stock control does.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> OpticalCorrections = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ChipPadding"] = "makes a 16 px Caption line a 20 px chip, Fluent's medium badge",
        ["LinkPadding"] = "makes a 20 px line of link text a 24 px target (WCAG 2.5.8) without growing the row",
        ["InlineLinkPadding"] = "the same target for a link whose words line up with the text around it",
        ["FirstLineOffset"] = "sets a glyph or check box level with the first line of wrapping text",
        ["HeroFigureBaselineOffset"] = "lifts Body text onto a HeroFigure's baseline",
        ["FigureBaselineOffset"] = "lifts Body text onto a Figure's baseline",
        ["ToggleSwitchAreaInset"] = "the SDK's own inset of a toggle switch's plate, so the settings switch draws as the stock one",
    };

    [Fact]
    public void SpacingSitsOnTheFourPixelGrid()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements.SelectMany(element => Properties
            .Select(property => (Property: property, Value: ShellMarkup.Literal(element, property) ?? ShellMarkup.SetterLiteral(element, property)))
            .Where(set => set.Value is not null && !OnTheGrid(set.Value))
            .Select(set => file.At(element, $"{set.Property}=\"{set.Value}\" is off the 4 px grid"))));

        RuleBaseline.Hold("margins, padding and spacing are multiples of 4 (or a 1 px hairline)", found, new Dictionary<string, int>());
    }

    /// <summary>
    /// A page that takes a role's resource is on the grid only if the resource is, so each spacing
    /// resource is held to the grid too: every <c>Thickness</c>, and every <c>x:Double</c> named as a
    /// gap. Only a listed optical correction, declared in <c>App.xaml</c>, may be off it.
    /// </summary>
    [Fact]
    public void SpacingResourcesSitOnTheGridOrAreNamedOpticalCorrections()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(IsSpacingResource)
            .Where(resource => !OnTheGrid(resource.Value)
                && !(file.IsApplicationResources && OpticalCorrections.ContainsKey(resource.Attribute(Key)?.Value ?? string.Empty)))
            .Select(resource => file.At(resource, $"{resource.Attribute(Key)?.Value} is {resource.Value}, off the 4 px grid, and is not a listed optical correction")));

        RuleBaseline.Hold("a spacing resource is on the 4 px grid, or an optical correction listed with its reason", found, new Dictionary<string, int>());
    }

    [Fact]
    public void EveryListedOpticalCorrectionIsDeclaredInTheApplicationResources()
    {
        var declared = ApplicationResources().Keys.ToHashSet(StringComparer.Ordinal);

        Assert.All(OpticalCorrections.Keys, key => Assert.True(declared.Contains(key), $"{key} is listed as an optical correction but App.xaml declares no spacing resource by that name."));
    }

    /// <summary>A correction moved onto the grid is a role value like any other, so it leaves the list.</summary>
    [Fact]
    public void EveryListedOpticalCorrectionIsOffTheGrid()
    {
        var declared = ApplicationResources();

        Assert.All(
            OpticalCorrections.Keys.Where(declared.ContainsKey),
            key => Assert.False(OnTheGrid(declared[key]), $"{key} is {declared[key]}, on the 4 px grid: remove it from the optical corrections."));
    }

    [Fact]
    public void TheScaleNamesEveryRole()
    {
        var declared = ApplicationResources();

        Assert.All(
            ["LabelGap", "ControlGap", "ControlGapAbove", "RegionGap", "RegionGapAbove", "CardPadding", "CardRowPadding", "RowPadding"],
            role => Assert.True(declared.ContainsKey(role), $"App.xaml's spacing scale has no {role}."));
    }

    /// <summary>
    /// The control and region gaps are each written twice, as a length and as a margin, and each pair is
    /// one gap.
    /// </summary>
    [Theory]
    [InlineData("RegionGap")]
    [InlineData("ControlGap")]
    public void EachGapAndItsMarginAgree(string gap)
    {
        var declared = ApplicationResources();

        Assert.Equal(new double[] { 0, double.Parse(declared[gap], CultureInfo.InvariantCulture), 0, 0 }, ShellMarkup.Lengths(declared[gap + "Above"]));
    }

    /// <summary>The region gap is written a third time, as a margin under a part, and it is the same gap.</summary>
    [Fact]
    public void TheRegionGapBelowAPartIsTheRegionGap()
    {
        var declared = ApplicationResources();

        Assert.Equal(new double[] { 0, 0, 0, double.Parse(declared["RegionGap"], CultureInfo.InvariantCulture) }, ShellMarkup.Lengths(declared["RegionGapBelow"]));
    }

    private static Dictionary<string, string> ApplicationResources() => ShellMarkup.Files
        .Single(file => file.IsApplicationResources).Elements
        .Where(IsSpacingResource)
        .ToDictionary(element => element.Attribute(Key)!.Value, element => element.Value.Trim(), StringComparer.Ordinal);

    private static bool IsSpacingResource(XElement element) =>
        element.Attribute(Key)?.Value is { } key
        && (element.Name.LocalName == "Thickness" || (element.Name.LocalName == "Double" && key.EndsWith("Gap", StringComparison.Ordinal)));

    private static bool OnTheGrid(string value) => ShellMarkup.Lengths(value).All(length => length is 0 or 1 or -1 || Math.Abs(length % 4) == 0);
}
