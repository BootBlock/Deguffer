namespace Deguffer.Core.Tests;

/// <summary>
/// A status that changes is announced through one shared mechanism, never through a bare
/// <c>AutomationProperties.LiveSetting</c>. A live setting only declares how a client should treat a
/// live-region event, and nothing in the shell raises one, so a status line marked live may never be
/// read out, including the line that says a clean's §5.6 check failed. The count of bare live
/// settings may only go down, as each surface moves onto the mechanism that raises the event.
///
/// <para>WCAG 2.2 4.1.3 (status messages).</para>
/// </summary>
public sealed class DesignAnnouncementTests
{
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/CleanPage.xaml"] = 3,
        ["Deguffer.App/Views/DuplicatesPage.xaml"] = 4,
        ["Deguffer.App/Views/ExplorePage.xaml"] = 4,
        ["Deguffer.App/Views/HealthPage.xaml"] = 1,
        ["Deguffer.App/Views/MemoryPage.xaml"] = 4,
        ["Deguffer.App/Views/NetworkBrowserDialog.xaml"] = 1,
        ["Deguffer.App/Views/StartupPage.xaml"] = 1,
    };

    [Fact]
    public void NoStatusReliesOnABareLiveSetting()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => ShellMarkup.Sets(element, "AutomationProperties.LiveSetting"))
            .Select(element => file.At(element, $"<{element.Name.LocalName}> declares a live setting that nothing raises")));

        RuleBaseline.Hold("a changing status announces through the shared mechanism, not a bare LiveSetting", found, Baseline);
    }
}
