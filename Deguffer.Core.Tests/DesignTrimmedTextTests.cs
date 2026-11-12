namespace Deguffer.Core.Tests;

/// <summary>
/// Text that may be cut short with an ellipsis has a way to be read whole: a tooltip with its own
/// text, shown on hover and on keyboard focus. At large text sizes and in a narrow window a trimmed
/// name, path or figure is otherwise simply lost, and on a page that removes things the lost part is
/// often the part that tells two items apart.
///
/// <para>WCAG 2.2 1.4.4 and 1.4.10: truncate visibly and
/// recoverably. A tooltip set by any route counts, so the shared behaviour that shows one only
/// while the text is trimmed satisfies it once it exists.</para>
/// </summary>
public sealed class DesignTrimmedTextTests
{
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Controls/DriveTemplates.xaml"] = 1,
        ["Deguffer.App/Views/CleanPage.xaml"] = 2,
        ["Deguffer.App/Views/DuplicatesPage.xaml"] = 2,
        ["Deguffer.App/Views/ExplorePage.xaml"] = 10,
        ["Deguffer.App/Views/HealthPage.xaml"] = 1,
        ["Deguffer.App/Views/InstalledAppsPage.xaml"] = 4,
        ["Deguffer.App/Views/ItemListView.xaml"] = 2,
        ["Deguffer.App/Views/MapAppearanceView.xaml"] = 2,
        ["Deguffer.App/Views/MemoryPage.xaml"] = 6,
        ["Deguffer.App/Views/NetworkBrowserDialog.xaml"] = 1,
        ["Deguffer.App/Views/SettingsPage.xaml"] = 3,
        ["Deguffer.App/Views/StartupPage.xaml"] = 2,
    };

    [Fact]
    public void TrimmedTextCanBeReadWhole()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Name.LocalName == "TextBlock"
                && element.Attribute("TextTrimming")?.Value is { } trimming && trimming != "None")
            .Where(text => !ShellMarkup.Sets(text, "ToolTipService.ToolTip")
                && !text.Attributes().Any(attribute => attribute.Name.LocalName.StartsWith("TrimmedText.", StringComparison.Ordinal)))
            .Select(text => file.At(text, "a trimmed TextBlock has no tooltip to read it whole")));

        DesignBaseline.Hold("a TextBlock with TextTrimming can be read whole through a tooltip", found, Baseline);
    }
}
