using System.Globalization;

namespace Deguffer.Core.Tests;

/// <summary>
/// Translucency belongs on a background brush, never on an element that holds text or a control.
/// Element opacity applies to everything inside, so a panel at 0.75 draws its words and its buttons
/// at three-quarter strength over whatever is behind it, which on a map of arbitrary colours fails
/// the contrast text needs. A brush with an alpha lets the ground show through and keeps the words
/// whole.
///
/// <para>WCAG 2.2 1.4.3 and 1.4.11; §6.5's "the UI must stay fully legible with no translucency at
/// all". An opacity animated from code to fade an element in or out is not a literal in the markup,
/// so it is not caught here.</para>
/// </summary>
public sealed class DesignOpacityTests
{
    private static readonly IReadOnlySet<string> Legible = new HashSet<string>(StringComparer.Ordinal)
    {
        "TextBlock", "RichTextBlock", "TextBox", "InfoBar", "FontIcon", "SymbolIcon",
    };

    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/ExplorePage.xaml"] = 2,
    };

    [Fact]
    public void NothingThatHoldsTextOrAControlIsDrawnTranslucent()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Name.LocalName != "Setter"
                && ShellMarkup.Literal(element, "Opacity") is { } opacity
                && double.TryParse(opacity, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && value is > 0 and < 1)
            .Where(element => element.DescendantsAndSelf().Any(inner => Legible.Contains(inner.Name.LocalName) || ShellMarkup.IsPressable(inner)))
            .Select(element => file.At(element, $"<{element.Name.LocalName}> with Opacity=\"{element.Attribute("Opacity")!.Value}\" holds text or a control")));

        RuleBaseline.Hold("translucency is on a background brush, never element Opacity over text or a control", found, Baseline);
    }
}
