using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// The shell's XAML, read from the tree, for a design rule that holds across every page. The App's
/// controls cannot be loaded in a test process, but a literal font size, an unnamed button or a
/// fixed corner radius is a fact of the markup's text.
/// </summary>
internal static class ShellMarkup
{
    /// <summary>The controls a user presses, which is what a target size and an accessible name are about.</summary>
    public static readonly IReadOnlySet<string> Pressable = new HashSet<string>(StringComparer.Ordinal)
    {
        "Button", "ToggleButton", "RepeatButton", "HyperlinkButton", "DropDownButton", "SplitButton",
        "ToggleSplitButton", "AppBarButton", "AppBarToggleButton",
    };

    private static readonly IReadOnlySet<string> Icons = new HashSet<string>(StringComparer.Ordinal)
    {
        "FontIcon", "SymbolIcon", "PathIcon", "BitmapIcon", "ImageIcon", "Image",
    };

    private static readonly Lazy<IReadOnlyList<MarkupFile>> All = new(Load);

    /// <summary>Every XAML file in <c>Deguffer.App</c>, leaving out what a build wrote.</summary>
    public static IReadOnlyList<MarkupFile> Files => All.Value;

    /// <summary>The pages the navigation rail opens, which share one frame.</summary>
    public static IEnumerable<MarkupFile> Pages =>
        Files.Where(file => file.Name.StartsWith("Deguffer.App/Views/", StringComparison.Ordinal)
            && file.Name.EndsWith("Page.xaml", StringComparison.Ordinal));

    /// <summary>
    /// The value of an attribute written as a literal, or null where it is absent or set by markup
    /// such as <c>{StaticResource ...}</c> or <c>{x:Bind ...}</c>, which is the resource route the
    /// design rules ask for.
    /// </summary>
    public static string? Literal(XElement element, string attribute) =>
        element.Attribute(attribute)?.Value is { } value && !value.TrimStart().StartsWith('{') ? value : null;

    /// <summary>
    /// The literal value a style setter gives <paramref name="property"/>, or null where the element
    /// is not such a setter.
    /// </summary>
    public static string? SetterLiteral(XElement element, string property) =>
        element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value == property
            ? Literal(element, "Value")
            : null;

    /// <summary>Whether the element sets an attached or ordinary property, as an attribute or a property element.</summary>
    public static bool Sets(XElement element, string property) =>
        element.Attribute(property) is not null
        || element.Elements().Any(child => child.Name.LocalName == property);

    public static bool IsPressable(XElement element) => Pressable.Contains(element.Name.LocalName);

    /// <summary>
    /// A pressable control whose only content is a glyph, so nothing on screen names it and its
    /// accessible name and tooltip are the whole of what it says.
    /// </summary>
    public static bool IsIconOnly(XElement element)
    {
        if (!IsPressable(element) || element.Attribute("Content") is not null || element.Attribute("Label") is not null)
        {
            return false;
        }

        var content = element.Elements().Where(child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal)).ToList();

        return content.Count > 0
            && content.All(child => Icons.Contains(child.Name.LocalName))
            && element.Nodes().OfType<XText>().All(text => string.IsNullOrWhiteSpace(text.Value));
    }

    /// <summary>The numbers a thickness or a single length is written with, such as <c>0,14,0,0</c>.</summary>
    public static IEnumerable<double> Lengths(string value) =>
        value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var length) ? length : double.NaN);

    public static int LineOf(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    private static IReadOnlyList<MarkupFile> Load()
    {
        var app = Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.App");

        var files = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(file => new MarkupFile(
                Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/'),
                XDocument.Load(file, LoadOptions.SetLineInfo),
                File.ReadLines(file).Count()))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();

        // A sweep that silently stopped finding the markup would pass every rule. The shell had 22
        // XAML files when these rules were written.
        Assert.True(files.Count >= 20, $"Only {files.Count} XAML files were found, so the design rules are no longer reading the shell.");

        return files;
    }
}

/// <summary>One XAML file, named by its path from the repository root with forward slashes.</summary>
internal sealed record MarkupFile(string Name, XDocument Document, int Lines)
{
    public IEnumerable<XElement> Elements => Document.Descendants();

    public bool IsApplicationResources => Name == "Deguffer.App/App.xaml";

    public DesignViolation At(XObject node, string detail) => new(Name, ShellMarkup.LineOf(node), detail);
}
