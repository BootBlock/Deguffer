using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a narrow window cannot fit on one line goes onto further lines, never off the edge: a part
/// cut at the edge can be a tier chip reduced to letters that do not name a tier.
///
/// <para>On a line of its own, a <c>ReflowRow</c> offers each part the row's width and draws it no
/// wider. A horizontal <c>StackPanel</c> cannot fit itself to that: it asks for the sum of its
/// controls whatever it is offered, and loses the ones past the edge. A <c>FlowRow</c> wraps its
/// controls instead. For the same reason a <c>TextBlock</c> set to wrap inside a horizontal
/// <c>StackPanel</c> is offered unlimited width, so it never wraps unless it has a <c>MaxWidth</c>.</para>
///
/// <para>A pair that must stay on one line, such as Scan and the progress ring that keeps its place,
/// is counted in the baseline rather than wrapped.</para>
/// </summary>
public sealed class DesignWrappingTests
{
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/CleanPage.xaml"] = 1,
        ["Deguffer.App/Views/ExplorePage.xaml"] = 3,
    };

    [Fact]
    public void AReflowPartWrapsTheControlsItHolds()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Name.LocalName == "ReflowRow")
            .SelectMany(row => row.Elements())
            .Where(part => IsHorizontalStack(part) && Controls(part).Count() > 1)
            .Select(part => file.At(part, "a ReflowRow part holds controls in a horizontal StackPanel, which clips them on a narrow line")));

        RuleBaseline.Hold("a ReflowRow part that holds several controls wraps them in a FlowRow", found, Baseline);
    }

    [Fact]
    public void WrappingTextIsGivenAWidthToWrapIn()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(IsHorizontalStack)
            .SelectMany(Controls)
            .Where(text => text.Name.LocalName == "TextBlock"
                && text.Attribute("TextWrapping")?.Value is "Wrap" or "WrapWholeWords"
                && text.Attribute("MaxWidth") is null)
            .Select(text => file.At(text, "a wrapping TextBlock in a horizontal StackPanel has unlimited width, so it runs off the edge instead")));

        Assert.Empty(found);
    }

    /// <summary>
    /// A Storage row's name is the widest part of its line, and the part a narrow column cannot fit:
    /// in a FlowRow it is offered the whole line, so it must wrap its words to stay whole.
    /// </summary>
    [Fact]
    public void ARowsNameWrapsItsWords()
    {
        var page = ShellMarkup.Files.Single(file => file.Name == "Deguffer.App/Views/CleanPage.xaml");
        var names = page.Elements
            .Where(element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{x:Bind Name}"
                && element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "ReflowRow"))
            .ToList();

        Assert.Equal(2, names.Count);
        Assert.All(names, name => Assert.Equal("Wrap", name.Attribute("TextWrapping")?.Value));
    }

    /// <summary>
    /// A Storage row's chips sit inside a part of the row on both views, so the control's own panel
    /// must wrap too: the row's FlowRow cannot reach inside it.
    /// </summary>
    [Fact]
    public void ARowsChipsWrapOntoALineEach()
    {
        var chips = ShellMarkup.Files.Single(file => file.Name == "Deguffer.App/Controls/ProviderChips.xaml");
        var panel = chips.Document.Root!.Elements().Single();

        Assert.Equal("FlowRow", panel.Name.LocalName);
    }

    private static bool IsHorizontalStack(XElement element) =>
        element.Name.LocalName == "StackPanel" && element.Attribute("Orientation")?.Value == "Horizontal";

    /// <summary>The controls a panel holds, leaving out property elements such as <c>ToolTipService.ToolTip</c>.</summary>
    private static IEnumerable<XElement> Controls(XElement panel) =>
        panel.Elements().Where(child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal));
}
