namespace Deguffer.Core.Tests;

/// <summary>
/// Margins, padding and spacing are multiples of 4 effective pixels. Fluent lays every control out
/// on a 4 px grid, so a 10 or a 14 beside it puts edges and baselines a pixel or two out of line with
/// the controls around them, and the same gap comes out as 10 on one page and 16 on the next. A value
/// of 1 is a hairline, which the grid allows.
///
/// <para>Fluent's spacing guidance: every margin, padding and gap is a
/// multiple of 4.</para>
/// </summary>
public sealed class DesignSpacingTests
{
    private static readonly string[] Properties = ["Margin", "Padding", "Spacing", "RowSpacing", "ColumnSpacing"];

    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/App.xaml"] = 1,
        ["Deguffer.App/Controls/DriveTemplates.xaml"] = 4,
        ["Deguffer.App/Controls/ExploreOverview.xaml"] = 4,
        ["Deguffer.App/MainWindow.xaml"] = 1,
        ["Deguffer.App/Views/AboutPage.xaml"] = 8,
        ["Deguffer.App/Views/CleanConfirmationView.xaml"] = 2,
        ["Deguffer.App/Views/CleanPage.xaml"] = 32,
        ["Deguffer.App/Views/DuplicatesPage.xaml"] = 21,
        ["Deguffer.App/Views/ExplorePage.xaml"] = 18,
        ["Deguffer.App/Views/HealthPage.xaml"] = 4,
        ["Deguffer.App/Views/InstalledAppsPage.xaml"] = 5,
        ["Deguffer.App/Views/ItemListView.xaml"] = 9,
        ["Deguffer.App/Views/MapAppearanceView.xaml"] = 5,
        ["Deguffer.App/Views/MemoryPage.xaml"] = 5,
        ["Deguffer.App/Views/NetworkBrowserDialog.xaml"] = 4,
        ["Deguffer.App/Views/ProviderInfoView.xaml"] = 14,
        ["Deguffer.App/Views/SettingsPage.xaml"] = 21,
        ["Deguffer.App/Views/SettingsRow.xaml"] = 2,
        ["Deguffer.App/Views/StartupPage.xaml"] = 4,
    };

    [Fact]
    public void SpacingSitsOnTheFourPixelGrid()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements.SelectMany(element => Properties
            .Select(property => (Property: property, Value: ShellMarkup.Literal(element, property) ?? ShellMarkup.SetterLiteral(element, property)))
            .Where(set => set.Value is not null && ShellMarkup.Lengths(set.Value).Any(length => !OnTheGrid(length)))
            .Select(set => file.At(element, $"{set.Property}=\"{set.Value}\" is off the 4 px grid"))));

        DesignBaseline.Hold("margins, padding and spacing are multiples of 4 (or a 1 px hairline)", found, Baseline);
    }

    private static bool OnTheGrid(double length) => length is 0 or 1 or -1 || Math.Abs(length % 4) == 0;
}
