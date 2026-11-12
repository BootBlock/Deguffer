namespace Deguffer.Core.Tests;

/// <summary>
/// Every control a user presses is at least 24 by 24 effective pixels. A control with its minimum
/// size taken away shrinks to its glyph, and an 18 px button in the corner of a picture that pans on
/// drag is one a near miss turns into a pan. Setting <c>MinWidth</c> or <c>MinHeight</c> to 0 is
/// allowed only with an explicit size of 24 or more on that axis.
///
/// <para>WCAG 2.2 2.5.8 (target size, minimum).</para>
/// </summary>
public sealed class DesignTargetSizeTests
{
    private const double Minimum = 24;

    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Controls/ExploreOverview.xaml"] = 2,
        ["Deguffer.App/Views/InstalledAppsPage.xaml"] = 2,
        ["Deguffer.App/Views/StartupPage.xaml"] = 2,
    };

    [Fact]
    public void EveryPressableControlIsAtLeast24PixelsEachWay()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(ShellMarkup.IsPressable)
            .SelectMany(button => new[] { ("Width", "MinWidth"), ("Height", "MinHeight") }
                .Where(axis => TooSmall(button, axis.Item1, axis.Item2))
                .Select(axis => file.At(button, $"<{button.Name.LocalName}> can be smaller than {Minimum} px in {axis.Item1.ToLowerInvariant()}"))));

        RuleBaseline.Hold("a pressable control is at least 24 x 24 epx", found, Baseline);
    }

    private static bool TooSmall(System.Xml.Linq.XElement button, string size, string minimum)
    {
        var explicitSize = Number(ShellMarkup.Literal(button, size));

        if (explicitSize is { } set)
        {
            return set < Minimum;
        }

        return Number(ShellMarkup.Literal(button, minimum)) is { } floor && floor < Minimum;
    }

    private static double? Number(string? value) =>
        value is null ? null : ShellMarkup.Lengths(value).FirstOrDefault(double.NaN) is var length && !double.IsNaN(length) ? length : null;
}
