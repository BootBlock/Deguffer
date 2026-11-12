namespace Deguffer.Core.Tests;

/// <summary>
/// A corner radius comes from a resource, defined once in <c>App.xaml</c>. Fluent has two radii, one
/// for controls and one for overlays and cards, and the shell had six shapes when this rule was
/// written because each card was drawn by hand. One card shape across the pages is part of what
/// makes them read as one application.
///
/// <para>Fluent's geometry guidance: corners come from ControlCornerRadius and OverlayCornerRadius.</para>
/// </summary>
public sealed class DesignShapeTests
{
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Controls/ColumnBar.xaml"] = 1,
        ["Deguffer.App/Controls/ExploreOverview.xaml"] = 1,
        ["Deguffer.App/Views/DuplicatesPage.xaml"] = 2,
        ["Deguffer.App/Views/ExplorePage.xaml"] = 10,
        ["Deguffer.App/Views/MapAppearanceView.xaml"] = 3,
        ["Deguffer.App/Views/MemoryPage.xaml"] = 2,
        ["Deguffer.App/Views/SettingsPage.xaml"] = 4,
    };

    [Fact]
    public void CornersComeFromAResource()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => ShellMarkup.Literal(element, "CornerRadius") is not null
                || (!file.IsApplicationResources
                    && (ShellMarkup.SetterLiteral(element, "CornerRadius") is not null
                        || (element.Name.LocalName == "CornerRadius" && !element.HasElements))))
            .Select(element => file.At(element, $"<{element.Name.LocalName}> sets a literal corner radius")));

        RuleBaseline.Hold("a corner radius comes from a resource in App.xaml, never a literal", found, Baseline);
    }
}
