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
    /// A corner is one of the shell's four shapes: Fluent's two radii and the two named in
    /// <c>App.xaml</c>. A literal, or a resource of any other name, is a shape nobody chose for the
    /// whole shell, and the next page copies it. A radius bound to a template or computed for a
    /// piece of a card (<c>CardRows</c>) is still one of these shapes, so a binding passes.
    /// </summary>
    [Fact]
    public void CornersAreOneOfTheNamedShapes()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Select(element => (element, problem: CornerProblem(file, element)))
            .Where(pair => pair.problem is not null)
            .Select(pair => file.At(pair.element, $"<{pair.element.Name.LocalName}> {pair.problem}")));

        RuleBaseline.Hold("a corner radius is ControlCornerRadius, OverlayCornerRadius, PillCornerRadius, ColumnCornerRadius or ToggleSwitchKnobCornerRadius", found, new Dictionary<string, int>());
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

    private static readonly HashSet<string> FluentShapes = new(StringComparer.Ordinal) { "ControlCornerRadius", "OverlayCornerRadius" };

    /// <summary>The shapes <c>App.xaml</c> defines for itself, beside Fluent's two.</summary>
    private static readonly HashSet<string> ShellShapes = new(StringComparer.Ordinal) { "PillCornerRadius", "ColumnCornerRadius", "ToggleSwitchKnobCornerRadius" };

    private static readonly XName Key = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");

    private static string? CornerProblem(MarkupFile file, XElement element)
    {
        var name = element.Name.LocalName;

        if (name == "CornerRadius" && !element.HasElements)
        {
            return file.IsApplicationResources && element.Parent?.Name.LocalName == "ResourceDictionary"
                && element.Attribute(Key)?.Value is { } key && ShellShapes.Contains(key)
                    ? null
                    : "defines a corner shape the shell does not name";
        }

        if (name.EndsWith(".CornerRadius", StringComparison.Ordinal)
            || (name == "Setter.Value" && element.Parent?.Attribute("Property")?.Value == "CornerRadius"))
        {
            return element.HasElements ? null : "sets a literal corner radius";
        }

        var value = element.Attribute("CornerRadius")?.Value
            ?? (element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value == "CornerRadius" ? element.Attribute("Value")?.Value : null);

        return value is null ? null : ValueProblem(value);
    }

    private static string? ValueProblem(string value)
    {
        var match = ResourceReference().Match(value);

        if (match.Success)
        {
            var key = match.Groups["key"].Value;
            return FluentShapes.Contains(key) || ShellShapes.Contains(key) ? null : $"takes its corner from {key}, which is not a named shape";
        }

        return value.TrimStart().StartsWith('{') ? null : "sets a literal corner radius";
    }

    [GeneratedRegex(@"^\s*\{\s*(?:StaticResource|ThemeResource)\s+(?:ResourceKey\s*=\s*)?(?<key>\w+)\s*\}\s*$")]
    private static partial Regex ResourceReference();

    /// <summary>The brushes Fluent draws a card or a layer with: its stroke and its fills.</summary>
    [GeneratedRegex(@"\b(?:CardStrokeColor|CardBackgroundFillColor|LayerFillColor|LayerOnAcrylicFillColor|LayerOnMicaBaseAltFillColor|SolidBackgroundFillColorTertiary)\w*Brush\b")]
    private static partial Regex CardBrush();
}
