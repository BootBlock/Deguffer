using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every window that draws into its title bar (§6.5) hands Windows the platform <c>TitleBar</c>
/// control as its drag region, never a row drawn by hand. The control is as tall as the caption
/// buttons beside it, so the window's name lines up with their glyphs, and it dims the name and icon
/// while the window is in the background, as Windows dims its own (#928). A row drawn by hand does
/// neither unless it repeats both, and every window would keep its own copy of that markup.
///
/// <para>A window is found by its <c>SetTitleBar(...)</c> call, and its drag region by the
/// <c>x:Name</c> that call names.</para>
/// </summary>
public sealed partial class DesignTitleBarTests
{
    private const string PlatformTitleBar = "TitleBar";

    private static readonly XName XamlName = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");

    /// <summary>Every element in the shell's markup that carries an <c>x:Name</c>, by that name.</summary>
    private static readonly ILookup<string, NamedElement> NamedElements = ShellMarkup.Files
        .SelectMany(file => file.Elements.SelectMany(element => element.Attribute(XamlName) is { } name
            ? [new NamedElement(file, element, name.Value)]
            : Array.Empty<NamedElement>()))
        .ToLookup(entry => entry.Name, StringComparer.Ordinal);

    [Fact]
    public void EveryWindowsDragRegionIsThePlatformTitleBar()
    {
        var regions = DragRegions().ToList();

        var undeclared = regions
            .Where(region => !NamedElements[region.Name].Any())
            .Select(region => new RuleViolation(region.Source, region.Line, $"SetTitleBar names \"{region.Name}\", which no markup declares"));

        // Every element the call could mean, not the first: the windows share a name for their
        // drag regions, and a call is not tied to its markup file by anything the text says.
        var drawn = regions
            .Select(region => region.Name)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(name => NamedElements[name])
            .Where(entry => entry.Element.Name.LocalName != PlatformTitleBar)
            .Select(entry => entry.File.At(entry.Element, $"the drag region <{entry.Element.Name.LocalName} x:Name=\"{entry.Name}\"> is not the platform <{PlatformTitleBar}>"));

        RuleBaseline.Hold("a window's drag region is the platform TitleBar", undeclared.Concat(drawn), new Dictionary<string, int>());
    }

    // A sweep that stopped finding the calls would pass the rule above with nothing to check. Both
    // windows Deguffer opens draw into their title bars, so there are at least two.
    [Fact]
    public void TheSweepFindsEveryWindowsTitleBar()
    {
        var count = DragRegions().Count();

        Assert.True(count >= 2, $"Only {count} SetTitleBar calls were found, so the rule is no longer reading the windows.");
    }

    private static IEnumerable<DragRegion> DragRegions() =>
        TestProjects.SourceFiles(Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.App"))
            .SelectMany(file => ProductSources.NumberedCode(File.ReadAllText(file).ReplaceLineEndings("\n"))
                .SelectMany(code => SetTitleBarCall().Matches(code.Line)
                    .Select(match => new DragRegion(
                        Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/'),
                        code.Number,
                        match.Groups["name"].Value))));

    private sealed record NamedElement(MarkupFile File, XElement Element, string Name);

    private sealed record DragRegion(string Source, int Line, string Name);

    // The last identifier of the argument: SetTitleBar(TitleBar) and SetTitleBar(_view.TitleBar) both
    // name the element TitleBar.
    [GeneratedRegex(@"\bSetTitleBar\(\s*(?:[\w.]+\.)?(?<name>\w+)\s*\)")]
    private static partial Regex SetTitleBarCall();
}
