using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// A card is drawn by a card style, and a corner radius comes from a resource, both defined once in
/// <c>App.xaml</c>. Fluent has two radii, one for controls and one for overlays and cards, and the
/// shell had six shapes and three card fills when these rules were written, because each card was
/// drawn by hand: a Storage card beside an Explore card beside a Settings row read as three
/// applications. One card shape across the pages is part of what makes them read as one.
///
/// <para>Fluent's geometry guidance: corners come from ControlCornerRadius and OverlayCornerRadius,
/// and a card takes the card stroke over a layer or card fill.</para>
/// </summary>
public sealed partial class DesignShapeTests
{
    /// <summary>
    /// A literal radius is allowed only as the value of a <c>CornerRadius</c> resource in
    /// <c>App.xaml</c>, which is what names a shape. A literal in a style setter, even one in
    /// <c>App.xaml</c>, is a shape with no name, and the next page copies the number.
    /// </summary>
    [Fact]
    public void CornersComeFromAResource()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => ShellMarkup.Literal(element, "CornerRadius") is not null
                || ShellMarkup.SetterLiteral(element, "CornerRadius") is not null
                || (element.Name.LocalName == "CornerRadius" && !element.HasElements && !IsNamedShape(file, element)))
            .Select(element => file.At(element, $"<{element.Name.LocalName}> sets a literal corner radius")));

        RuleBaseline.Hold("a corner radius comes from a resource in App.xaml, never a literal", found, new Dictionary<string, int>());
    }

    /// <summary>
    /// A card's stroke and fill are named only inside a style in <c>App.xaml</c>, so a card on any
    /// page is one of the shared card styles rather than a border that copies, and drifts from, the
    /// look of one.
    /// </summary>
    [Fact]
    public void CardsAreDrawnByASharedStyle()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => !(file.IsApplicationResources && element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "Style")))
            .SelectMany(element => element.Attributes()
                .Select(attribute => CardBrush().Match(attribute.Value))
                .Where(match => match.Success)
                .Select(match => file.At(element, $"<{element.Name.LocalName}> draws a card with {match.Value} instead of a card style"))));

        RuleBaseline.Hold("a card's stroke and fill are named only in a card style in App.xaml", found, new Dictionary<string, int>());
    }

    private static bool IsNamedShape(MarkupFile file, XElement element) =>
        file.IsApplicationResources
        && element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is not null
        && element.Parent?.Name.LocalName == "ResourceDictionary";

    /// <summary>The brushes Fluent draws a card or a layer with: its stroke and its fills.</summary>
    [GeneratedRegex(@"\b(?:CardStrokeColor|CardBackgroundFillColor|LayerFillColor|LayerOnAcrylicFillColor|LayerOnMicaBaseAltFillColor|SolidBackgroundFillColorTertiary)\w*Brush\b")]
    private static partial Regex CardBrush();
}
