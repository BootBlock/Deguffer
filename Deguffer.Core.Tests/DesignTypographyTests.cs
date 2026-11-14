namespace Deguffer.Core.Tests;

/// <summary>
/// Text takes its size from the type ramp's styles in <c>App.xaml</c>, never from a literal font
/// size on an element. A size chosen element by element is how a section heading came to be 15 on
/// one page and 20 on the next, and secondary text 12, 13 or 14 for the same role: the hierarchy a
/// reader uses to find their way drifts from page to page. A glyph's size is a named resource too.
///
/// <para>Fluent's type ramp: text uses the ramp,
/// never a literal size. A style setter in <c>App.xaml</c> is where the ramp is defined, so it is
/// the one place a literal size belongs.</para>
/// </summary>
public sealed class DesignTypographyTests
{
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Controls/DriveTemplates.xaml"] = 3,
        ["Deguffer.App/Controls/ExploreOverview.xaml"] = 2,
        ["Deguffer.App/MainWindow.xaml"] = 1,
        ["Deguffer.App/Views/AboutPage.xaml"] = 13,
        ["Deguffer.App/Views/CleanPage.xaml"] = 13,
        ["Deguffer.App/Views/DuplicatesPage.xaml"] = 22,
        ["Deguffer.App/Views/ExplorePage.xaml"] = 34,
        ["Deguffer.App/Views/InstalledAppsPage.xaml"] = 2,
        ["Deguffer.App/Views/ItemListView.xaml"] = 12,
        ["Deguffer.App/Views/MapAppearanceView.xaml"] = 2,
        ["Deguffer.App/Views/MemoryPage.xaml"] = 3,
        ["Deguffer.App/Views/NetworkBrowserDialog.xaml"] = 3,
        ["Deguffer.App/Views/ProviderInfoView.xaml"] = 9,
        ["Deguffer.App/Views/SettingsPage.xaml"] = 16,
        ["Deguffer.App/Views/SettingsRow.xaml"] = 2,
        ["Deguffer.App/Views/StartupPage.xaml"] = 4,
    };

    [Fact]
    public void TextTakesItsSizeFromTheTypeRamp()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => ShellMarkup.Literal(element, "FontSize") is not null
                || (!file.IsApplicationResources && ShellMarkup.SetterLiteral(element, "FontSize") is not null))
            .Select(element => file.At(element, $"<{element.Name.LocalName}> sets a literal font size")));

        RuleBaseline.Hold("text takes its size from a ramp style or a named resource, never a literal FontSize", found, Baseline);
    }
}
