using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every window that draws into its title bar (§6.5) hands Windows the platform <c>TitleBar</c>
/// control as its drag region, never a row drawn by hand. The hand-drawn rows were 40 px under
/// 32 px caption buttons, so the window's name sat below the minimise, maximise and close glyphs,
/// and nothing dimmed it while the window was in the background, as Windows dims its own (#928).
/// The control matches the caption height and follows activation itself, and both windows share it
/// rather than keeping a copy of the same markup each.
///
/// <para>A window is found by its <c>SetTitleBar(...)</c> call, and its drag region by the
/// <c>x:Name</c> that call names.</para>
/// </summary>
public sealed partial class DesignTitleBarTests
{
    private const string PlatformTitleBar = "TitleBar";

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
            .Select(entry => entry.File.At(entry.Element, $"the drag region <{entry.Element.Name.LocalName} x:Name=\"{entry.Element.Attribute(XamlName)!.Value}\"> is not the platform <{PlatformTitleBar}>"));

        RuleBaseline.Hold("a window's drag region is the platform TitleBar", undeclared.Concat(drawn), new Dictionary<string, int>());
    }

    // A sweep that stopped finding the calls would pass the rule above with nothing to check. The
    // main window and the map appearance window each set one when this was written.
    [Fact]
    public void TheSweepFindsEveryWindowsTitleBar()
    {
        var count = DragRegions().Count();

        Assert.True(count >= 2, $"Only {count} SetTitleBar calls were found, so the rule is no longer reading the windows.");
    }

    private static ILookup<string, (MarkupFile File, XElement Element)> NamedElements =>
        ShellMarkup.Files
            .SelectMany(file => file.Elements
                .Where(element => element.Attribute(XamlName) is not null)
                .Select(element => (File: file, Element: element)))
            .ToLookup(entry => entry.Element.Attribute(XamlName)!.Value, StringComparer.Ordinal);

    private static IEnumerable<DragRegion> DragRegions() =>
        TestProjects.SourceFiles(Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.App"))
            .SelectMany(file => ProductSources.NumberedCode(File.ReadAllText(file).ReplaceLineEndings("\n"))
                .SelectMany(code => SetTitleBarCall().Matches(code.Line)
                    .Select(match => new DragRegion(
                        Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/'),
                        code.Number,
                        match.Groups["name"].Value))));

    private static readonly XName XamlName = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");

    private sealed record DragRegion(string Source, int Line, string Name);

    // The last identifier of the argument: SetTitleBar(TitleBar) and SetTitleBar(_view.TitleBar) both
    // name the element TitleBar.
    [GeneratedRegex(@"\bSetTitleBar\(\s*(?:[\w.]+\.)?(?<name>\w+)\s*\)")]
    private static partial Regex SetTitleBarCall();
}
