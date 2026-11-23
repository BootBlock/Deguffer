using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// Text takes its size from the type ramp's styles in <c>App.xaml</c>, never from a font size on an
/// element. A size chosen element by element is how a section heading came to be 15 on one page and
/// 20 on the next, and secondary text 12, 13 or 14 for the same role: the hierarchy a reader uses to
/// find their way drifted from page to page, and nobody could change "the section heading" because
/// there was no single one. A glyph's size is a named resource too, so an icon keeps its proportion
/// to the text beside it.
///
/// <para>Fluent's type ramp: text uses the ramp, never a literal size. The ramp's styles name Fluent's
/// own steps by their resources, so no style can invent a size, and Caption, the smallest step, is
/// the floor: nothing is drawn below 12.</para>
/// </summary>
public sealed partial class DesignTypographyTests
{
    /// <summary>Fluent's type ramp, by the resources its own text styles read. Caption, at 12, is its smallest step.</summary>
    private static readonly IReadOnlySet<string> FluentRamp = new HashSet<string>(StringComparer.Ordinal)
    {
        "CaptionTextBlockFontSize", "BodyTextBlockFontSize", "BodyLargeTextBlockFontSize", "SubtitleTextBlockFontSize",
        "TitleTextBlockFontSize", "TitleLargeTextBlockFontSize", "DisplayTextBlockFontSize",
    };

    private static readonly XName Key = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");

    /// <summary>The glyph sizes <c>App.xaml</c> declares, the only sizes a glyph may take.</summary>
    private static readonly Lazy<IReadOnlySet<string>> GlyphSizes = new(() => ShellMarkup.Files
        .Single(file => file.IsApplicationResources).Elements
        .Where(element => element.Name.LocalName == "Double"
            && element.Attribute(Key)?.Value.StartsWith("GlyphSize", StringComparison.Ordinal) == true)
        .Select(element => element.Attribute(Key)!.Value)
        .ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void TextTakesItsSizeFromTheTypeRamp()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Attribute("FontSize") is not null)
            .Select(element => (file, element, detail: AttributeBreak(element)))
            .Concat(file.IsApplicationResources
                ? []
                : file.Elements.Select(element => (file, element, detail: PageSetterBreak(element))))
            .Where(place => place.detail is not null)
            .Select(place => place.file.At(place.element, place.detail!)));

        RuleBaseline.Hold("text takes its size from a ramp style in App.xaml, and a glyph from a named GlyphSize resource", found, new Dictionary<string, int>());
    }

    [Fact]
    public void TheRampStylesNameFluentStepsAndDeclaredGlyphSizes()
    {
        var app = ShellMarkup.Files.Single(file => file.IsApplicationResources);

        var found = app.Elements
            .Where(element => IsFontSizeSetter(element))
            .Where(setter => !(TargetsIcon(setter)
                ? IsDeclaredGlyphSize(setter.Attribute("Value")?.Value)
                : FluentRamp.Contains(ResourceKey(setter.Attribute("Value")?.Value) ?? string.Empty)))
            .Select(setter => app.At(setter, TargetsIcon(setter)
                ? "a glyph style's FontSize setter does not name a GlyphSize resource declared here"
                : "a text style's FontSize setter does not name a step of Fluent's type ramp"));

        RuleBaseline.Hold("every FontSize setter in App.xaml names a Fluent ramp step, or a declared GlyphSize for a glyph", found, new Dictionary<string, int>());
    }

    private static string? AttributeBreak(XElement element)
    {
        var value = element.Attribute("FontSize")!.Value;

        if (ShellMarkup.IsIcon(element.Name.LocalName))
        {
            return IsDeclaredGlyphSize(value) ? null : $"<{element.Name.LocalName}> sizes its glyph with {value}, not a GlyphSize resource";
        }

        return element.Attribute("Style") is not null
            ? $"<{element.Name.LocalName}> sets FontSize {value} beside its Style, overriding the ramp"
            : $"<{element.Name.LocalName}> sets FontSize {value} rather than taking a ramp style";
    }

    /// <summary>
    /// A page's own style may size a glyph from a declared resource, and may reset the size a
    /// container hands down to its text to a ramp step, as a list header must. It may not size text
    /// itself, because that is a second ramp.
    /// </summary>
    private static string? PageSetterBreak(XElement element)
    {
        if (!IsFontSizeSetter(element))
        {
            return null;
        }

        var value = element.Attribute("Value")?.Value;

        if (TargetsIcon(element))
        {
            return IsDeclaredGlyphSize(value) ? null : $"a glyph style sizes its glyph with {value}, not a GlyphSize resource";
        }

        var target = element.Parent?.Attribute("TargetType")?.Value;

        return target != "TextBlock" && FluentRamp.Contains(ResourceKey(value) ?? string.Empty)
            ? null
            : $"a {target} style on a page sets FontSize {value}; text sizes belong to App.xaml's ramp";
    }

    private static bool IsFontSizeSetter(XElement element) =>
        element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value == "FontSize";

    private static bool TargetsIcon(XElement setter) =>
        setter.Parent?.Attribute("TargetType")?.Value is { } target && ShellMarkup.IsIcon(target);

    private static bool IsDeclaredGlyphSize(string? value) =>
        ResourceKey(value) is { } key && GlyphSizes.Value.Contains(key);

    private static string? ResourceKey(string? value) =>
        value is not null && StaticResource().Match(value) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"^\{StaticResource\s+(\w+)\}$")]
    private static partial Regex StaticResource();
}
