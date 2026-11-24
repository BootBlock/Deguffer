using System.Xml.Linq;
using Deguffer.Core.Configuration;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Settings page's rows and the list of settings the page restores are the same settings by the
/// same titles. A row that sets a value states its default and offers a reset, so a reader can tell
/// whether a setting that widens or narrows what gets deleted is still on its shipped value. A row the
/// list left out would keep its value through a restore while the dialog said every setting went back,
/// and a setting the list held with no row would be changed by a restore the reader could not see.
/// </summary>
public sealed class ShippedSettingsMarkupTests
{
    private static readonly string[] SettingsMarkup =
        ["Deguffer.App/Views/SettingsPage.xaml", "Deguffer.App/Views/ScanSettingsSection.xaml"];

    /// <summary>The controls that hold a value, as distinct from a button that acts or opens.</summary>
    private static readonly string[] ValueControls = ["ToggleSwitch", "NumberBox", "ComboBox"];

    [Fact]
    public void EveryRowThatStatesADefaultIsASettingThePageRestores() =>
        Assert.Equal(
            ShippedSettings.All.Select(setting => setting.Title).Distinct().Order(StringComparer.Ordinal),
            Rows().Where(row => row.Attribute("Default") is not null).Select(Title).Order(StringComparer.Ordinal));

    /// <summary>
    /// Every row whose control holds a value states its default. The kind of drive is the one exception:
    /// it is where the reader is looking, and is not stored.
    /// </summary>
    [Fact]
    public void EveryRowThatSetsAValueStatesItsDefault()
    {
        var unstated = Rows()
            .Where(row => row.Attribute("Default") is null && HoldsAValue(row) && !PicksTheKindOfDrive(row))
            .Select(Title)
            .ToList();

        Assert.True(unstated.Count == 0, $"These rows set a value but state no default: {string.Join(", ", unstated)}.");
    }

    private static IEnumerable<XElement> Rows() => ShellMarkup.Files
        .Where(file => SettingsMarkup.Contains(file.Name))
        .SelectMany(file => file.Elements)
        .Where(element => element.Name.LocalName == "SettingsRow");

    private static string Title(XElement row) => row.Attribute("Title")!.Value;

    private static bool HoldsAValue(XElement row) =>
        row.Descendants().Any(element => ValueControls.Contains(element.Name.LocalName));

    private static bool PicksTheKindOfDrive(XElement row) =>
        row.Descendants().Any(element => element.Attribute("SelectedIndex")?.Value.Contains("Scanning.KindIndex", StringComparison.Ordinal) == true);
}
