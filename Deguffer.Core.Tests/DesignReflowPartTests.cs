using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// A part of a <c>ReflowRow</c> that holds several controls wraps them, so a part wider than its line
/// on a narrow window goes onto further lines rather than being clipped at the row's edge.
///
/// <para>On a line of its own, a <c>ReflowRow</c> offers each part the row's width and draws it no
/// wider. A horizontal <c>StackPanel</c> cannot fit itself to that: it asks for the sum of its
/// controls whatever it is offered, and loses the ones past the edge. On a Storage row that clipped
/// the name, cut the tier chip to its first letters, which no longer named its tier, and drew the
/// "What is this?" link as "W". A <c>FlowRow</c> wraps its controls instead.</para>
///
/// <para>A pair that must stay on one line, such as Scan and the progress ring that keeps its place,
/// is counted in the baseline rather than wrapped.</para>
/// </summary>
public sealed class DesignReflowPartTests
{
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/CleanPage.xaml"] = 1,
        ["Deguffer.App/Views/ExplorePage.xaml"] = 4,
    };

    [Fact]
    public void AReflowPartWrapsTheControlsItHolds()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Name.LocalName == "ReflowRow")
            .SelectMany(row => row.Elements())
            .Where(IsUnwrappedRow)
            .Select(part => file.At(part, "a ReflowRow part holds controls in a horizontal StackPanel, which clips them on a narrow line")));

        RuleBaseline.Hold("a ReflowRow part that holds several controls wraps them in a FlowRow", found, Baseline);
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

    private static bool IsUnwrappedRow(XElement part) =>
        part.Name.LocalName == "StackPanel"
        && part.Attribute("Orientation")?.Value == "Horizontal"
        && part.Elements().Count(child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal)) > 1;
}
