namespace Deguffer.Core.Tests;

/// <summary>
/// Everything a screen reader lands on says what it is. A button that shows only a glyph has no
/// words on screen, so its accessible name and its tooltip are all it says, to a screen reader and to
/// a sighted user wondering what the glyph means. A busy indicator with no name is announced as
/// "progress ring" with nothing to say what is busy. A chip's template hides its label from UI
/// Automation (<c>App.xaml</c>), so a chip that is not named, or hidden as decoration, is announced
/// with no name at all: on Health that was a failing drive's verdict.
///
/// <para>WCAG 2.2 4.1.2 (name, role, value) and 1.1.1, and Fluent's guidance on icon buttons.</para>
/// </summary>
public sealed class DesignAccessibleNameTests
{
    private static readonly Dictionary<string, int> ProgressBaseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/CleanPage.xaml"] = 1,
        ["Deguffer.App/Views/DuplicatesPage.xaml"] = 1,
        ["Deguffer.App/Views/ExplorePage.xaml"] = 1,
        ["Deguffer.App/Views/HealthPage.xaml"] = 1,
        ["Deguffer.App/Views/InstalledAppsPage.xaml"] = 1,
        ["Deguffer.App/Views/StartupPage.xaml"] = 1,
    };

    private static readonly Dictionary<string, int> ChipBaseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/HealthPage.xaml"] = 2,
    };

    [Fact]
    public void EveryIconOnlyButtonHasANameAndATooltip()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(ShellMarkup.IsIconOnly)
            .Where(button => !ShellMarkup.Sets(button, "AutomationProperties.Name") || !ShellMarkup.Sets(button, "ToolTipService.ToolTip"))
            .Select(button => file.At(button, $"icon-only <{button.Name.LocalName}> lacks an AutomationProperties.Name or a ToolTipService.ToolTip")));

        DesignBaseline.Hold("an icon-only button has an accessible name and a tooltip", found, new Dictionary<string, int>());
    }

    [Fact]
    public void EveryBusyIndicatorIsNamedOrHidden()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Name.LocalName is "ProgressRing" or "ProgressBar")
            .Where(element => !IsNamedOrRaw(element))
            .Select(element => file.At(element, $"<{element.Name.LocalName}> has no accessible name and is not hidden as decoration")));

        DesignBaseline.Hold("a progress indicator is named, or Raw beside text that says the same", found, ProgressBaseline);
    }

    [Fact]
    public void EveryChipIsNamedHiddenOrInsideANamedButton()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Name.LocalName == "ContentControl"
                && (element.Attribute("Style")?.Value.Contains("Chip", StringComparison.Ordinal) ?? false))
            .Where(chip => !IsNamedOrRaw(chip)
                && !chip.Ancestors().Any(ancestor => ShellMarkup.IsPressable(ancestor) && ShellMarkup.Sets(ancestor, "AutomationProperties.Name")))
            .Select(chip => file.At(chip, "a chip has no accessible name, is not Raw and is not inside a named button")));

        DesignBaseline.Hold("a chip sets AutomationProperties.Name, or is Raw, or sits in a named button", found, ChipBaseline);
    }

    private static bool IsNamedOrRaw(System.Xml.Linq.XElement element) =>
        ShellMarkup.Sets(element, "AutomationProperties.Name")
        || element.Attribute("AutomationProperties.AccessibilityView")?.Value == "Raw";
}
