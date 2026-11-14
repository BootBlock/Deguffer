using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every control a user operates has a UI Automation id, so a script, a test or an assistive tool
/// reaches it by what it is rather than by what it says. A name is the visible text, which changes
/// with the wording and the state: the Elevate button reads "Elevate and scan" or "Elevate and
/// rescan". A control with neither was reached by a keystroke, and Alt+Down on the Scan split button
/// starts a scan of every location when it lands on the primary part instead of the drop-down.
///
/// <para>Outside a template, <c>x:Name</c> is enough: WinUI reports it as the id where
/// <c>AutomationProperties.AutomationId</c> is not set. Inside a template the same literal would
/// repeat on every row, so the id there is bound to something the row's data holds, such as the
/// provider's id.</para>
///
/// <para>Issue #1055.</para>
/// </summary>
public sealed class DesignAutomationIdTests
{
    /// <summary>
    /// The controls a user operates, matched by the end of the element's name, so a control derived
    /// from one (<c>EnabledAwareSplitButton</c>) is held to the rule its base is.
    /// </summary>
    private static readonly string[] Operated =
    [
        "Button", "ComboBox", "TextBox", "AutoSuggestBox", "PasswordBox", "NumberBox", "CheckBox",
        "ToggleSwitch", "Slider",
    ];

    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/AboutPage.xaml"] = 3,
        ["Deguffer.App/Views/DuplicatesPage.xaml"] = 29,
        ["Deguffer.App/Views/ExplorePage.xaml"] = 23,
        ["Deguffer.App/Views/HealthPage.xaml"] = 2,
        ["Deguffer.App/Views/InstalledAppsPage.xaml"] = 10,
        ["Deguffer.App/Views/ItemListView.xaml"] = 5,
        ["Deguffer.App/Views/MemoryPage.xaml"] = 5,
        ["Deguffer.App/Views/NetworkBrowserDialog.xaml"] = 6,
        ["Deguffer.App/Views/ProviderInfoView.xaml"] = 1,
        ["Deguffer.App/Views/SettingsPage.xaml"] = 27,
        ["Deguffer.App/Views/StartupPage.xaml"] = 5,
    };

    [Fact]
    public void EveryOperatedControlHasAnAutomationId()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(IsOperated)
            .Where(control => !HasAutomationId(control))
            .Select(control => file.At(control, IsTemplated(control)
                ? $"<{control.Name.LocalName}> in a template has no AutomationProperties.AutomationId bound to its row's data"
                : $"<{control.Name.LocalName}> has neither an AutomationProperties.AutomationId nor an x:Name")));

        RuleBaseline.Hold("a control a user operates has an automation id, bound to its data inside a template", found, Baseline);
    }

    /// <summary>
    /// The rule's own reading of the Storage page, so a change to how the sweep matches cannot pass
    /// every file by matching nothing: the Scan split button is a derived control, and a row's tick is
    /// in a template.
    /// </summary>
    [Fact]
    public void TheRuleReadsTheScanButtonAndTheRowTicks()
    {
        var page = Assert.Single(ShellMarkup.Files, file => file.Name == "Deguffer.App/Views/CleanPage.xaml");
        var operated = page.Elements.Where(IsOperated).ToList();

        var scan = Assert.Single(operated, control => control.Name.LocalName == "EnabledAwareSplitButton");
        Assert.Equal("ScanButton", scan.Attribute("AutomationProperties.AutomationId")?.Value);

        var ticks = operated.Where(control => control.Name.LocalName == "CheckBox" && IsTemplated(control)).ToList();
        Assert.NotEmpty(ticks);
        Assert.All(ticks, tick => Assert.True(HasAutomationId(tick)));
    }

    private static bool IsOperated(XElement element)
    {
        var name = element.Name.LocalName;

        return !name.Contains('.', StringComparison.Ordinal)
            && Operated.Any(control => name.EndsWith(control, StringComparison.Ordinal));
    }

    private static bool HasAutomationId(XElement control)
    {
        var id = control.Attribute("AutomationProperties.AutomationId")?.Value;

        if (IsTemplated(control))
        {
            return id is not null && id.TrimStart().StartsWith('{');
        }

        return id is not null || control.Attributes().Any(attribute => attribute.Name.LocalName == "Name");
    }

    private static bool IsTemplated(XElement control) =>
        control.Ancestors().Any(ancestor => ancestor.Name.LocalName is "DataTemplate" or "ControlTemplate");
}
