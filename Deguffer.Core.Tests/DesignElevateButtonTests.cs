using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every button that restarts Deguffer as administrator wears <c>ElevateButtonStyle</c>, and nothing
/// else does. The offer is made on six pages, and a user who has learned its warm tint on one should
/// find it on the next; a button that kept the plain look was easy to miss beside Refresh, and a
/// plain button in the tint would claim to elevate when it does not. Every such button was styled
/// when this rule was written, so it holds with no exceptions.
///
/// <para>A button is known by its command: each page names its elevation command <c>Elevate…Command</c>.</para>
/// </summary>
public sealed partial class DesignElevateButtonTests
{
    private const string StyleReference = "{StaticResource ElevateButtonStyle}";

    [Fact]
    public void EveryElevationButtonWearsTheElevationStyle()
    {
        var found = ShellMarkup.Files.SelectMany(file => ElevationButtons(file)
            .Where(button => !WearsStyle(button))
            .Select(button => file.At(button, "an elevation button does not set Style=\"{StaticResource ElevateButtonStyle}\"")));

        RuleBaseline.Hold("an elevation button wears ElevateButtonStyle", found, new Dictionary<string, int>());
    }

    [Fact]
    public void OnlyAnElevationButtonWearsTheElevationStyle()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => WearsStyle(element) && !IsElevationButton(element))
            .Select(element => file.At(element, $"<{element.Name.LocalName}> wears ElevateButtonStyle but does not elevate")));

        RuleBaseline.Hold("only an elevation button wears ElevateButtonStyle", found, new Dictionary<string, int>());
    }

    // A sweep that stopped finding the buttons would pass the rule above with nothing to check.
    // The shell offered elevation on six pages when this was written.
    [Fact]
    public void TheSweepFindsTheElevationButtons()
    {
        var count = ShellMarkup.Files.Sum(file => ElevationButtons(file).Count());

        Assert.True(count >= 6, $"Only {count} elevation buttons were found, so the rule is no longer reading the pages.");
    }

    private static IEnumerable<XElement> ElevationButtons(MarkupFile file) => file.Elements.Where(IsElevationButton);

    private static bool IsElevationButton(XElement element) =>
        ShellMarkup.IsPressable(element)
        && element.Attribute("Command")?.Value is { } command
        && ElevationCommand().IsMatch(command);

    private static bool WearsStyle(XElement element) =>
        string.Equals(element.Attribute("Style")?.Value.Trim(), StyleReference, StringComparison.Ordinal);

    [GeneratedRegex(@"\bElevate\w*Command\b")]
    private static partial Regex ElevationCommand();
}
