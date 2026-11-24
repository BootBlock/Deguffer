using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Settings page's rows and the list of settings the page restores are the same settings. A row
/// that sets a value states its default and offers a reset, so a reader can tell whether a setting that
/// widens or narrows what gets deleted is still on its shipped value. A row the list left out would keep
/// its value through a restore while the dialog said every setting went back, a setting the list held
/// with no row would be changed by a restore the reader could not see, and a row whose reset belonged to
/// another setting would put back the wrong one.
///
/// <para>A row is matched to its setting by name: its default is bound to <c>&lt;Name&gt;Default</c>,
/// its control to <c>&lt;Name&gt;</c> (or <c>&lt;Name&gt;Index</c> for a picker), and the setting is
/// <see cref="ShippedSettings"/>' or <see cref="KindSettingDefaults"/>' member of that name.</para>
/// </summary>
public sealed partial class ShippedSettingsMarkupTests
{
    private static readonly string[] SettingsMarkup =
        ["Deguffer.App/Views/SettingsPage.xaml", "Deguffer.App/Views/ScanSettingsSection.xaml"];

    /// <summary>The controls that hold a value, and the attribute each holds it in.</summary>
    private static readonly Dictionary<string, string> ValueControls = new(StringComparer.Ordinal)
    {
        ["ToggleSwitch"] = "IsOn",
        ["NumberBox"] = "Value",
        ["ComboBox"] = "SelectedIndex",
        ["CheckBox"] = "IsChecked",
        ["Slider"] = "Value",
        ["RadioButtons"] = "SelectedIndex",
    };

    /// <summary>
    /// Every row whose control holds a value states its default. The kind of drive is the one exception:
    /// it is where the reader is looking, and is not stored.
    /// </summary>
    [Fact]
    public void EveryRowThatSetsAValueStatesItsDefault()
    {
        var unstated = Rows()
            .Where(row => row.Attribute("Default") is null && ValueControl(row) is not null && !PicksTheKindOfDrive(row))
            .Select(Title)
            .ToList();

        Assert.True(unstated.Count == 0, $"These rows set a value but state no default: {string.Join(", ", unstated)}.");
    }

    /// <summary>Each row's default and its control are bound to one setting, and that is a setting the page restores, under the row's title.</summary>
    [Fact]
    public void EachRowsDefaultIsTheSettingItsControlSets()
    {
        foreach (var row in Rows().Where(row => row.Attribute("Default") is not null))
        {
            var stated = Bound(row.Attribute("Default")!.Value);
            var control = ValueControl(row);

            Assert.True(stated is not null && stated.Value.Member.EndsWith("Default", StringComparison.Ordinal), $"'{Title(row)}' binds its default as {row.Attribute("Default")!.Value}.");
            Assert.True(control is not null, $"'{Title(row)}' states a default but holds no value control.");

            var name = stated.Value.Member[..^"Default".Length];
            var set = Bound(control!.Attribute(ValueControls[control.Name.LocalName])!.Value);

            Assert.True(
                set is not null && set.Value.Source == stated.Value.Source && (set.Value.Member == name || set.Value.Member == name + "Index"),
                $"'{Title(row)}' states the default of {name}, but its control is bound to {control.Attribute(ValueControls[control.Name.LocalName])!.Value}.");

            var setting = Setting(name);

            Assert.True(setting is not null, $"'{Title(row)}' states the default of {name}, which no ShippedSettings or KindSettingDefaults member is.");

            if (!Title(row).StartsWith('{'))
            {
                Assert.Equal(setting!.Title, Title(row));
            }
        }
    }

    /// <summary>Every setting the page restores has a row that states its default.</summary>
    [Fact]
    public void EverySettingThePageRestoresHasARow()
    {
        var stated = Rows()
            .Select(row => row.Attribute("Default")?.Value)
            .OfType<string>()
            .Select(Bound)
            .Select(binding => binding!.Value.Member[..^"Default".Length])
            .Order(StringComparer.Ordinal);

        var restored = typeof(ShippedSettings).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(SettingDefault))
            .Select(field => field.Name)
            .Concat(KindProperties().Select(property => property.Name))
            .Order(StringComparer.Ordinal);

        Assert.Equal(restored, stated);
    }

    private static IEnumerable<XElement> Rows() => ShellMarkup.Files
        .Where(file => SettingsMarkup.Contains(file.Name))
        .SelectMany(file => file.Elements)
        .Where(element => element.Name.LocalName == "SettingsRow");

    private static string Title(XElement row) => row.Attribute("Title")!.Value;

    private static XElement? ValueControl(XElement row) =>
        row.Descendants().FirstOrDefault(element => ValueControls.ContainsKey(element.Name.LocalName));

    private static bool PicksTheKindOfDrive(XElement row) =>
        row.Descendants().Any(element => element.Attribute("SelectedIndex")?.Value.Contains("Scanning.KindIndex", StringComparison.Ordinal) == true);

    /// <summary>The source and member an <c>x:Bind</c> names, such as <c>ViewModel</c> and <c>ThemeIndex</c>.</summary>
    private static (string Source, string Member)? Bound(string attribute) =>
        BindingPattern().Match(attribute) is { Success: true } match ? (match.Groups["source"].Value, match.Groups["member"].Value) : null;

    private static SettingDefault? Setting(string name) =>
        typeof(ShippedSettings).GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as SettingDefault
        ?? KindProperties().FirstOrDefault(property => property.Name == name)?.GetValue(ShippedSettings.ForKind(StorageMedia.Nvme)) as SettingDefault;

    private static IEnumerable<PropertyInfo> KindProperties() =>
        typeof(KindSettingDefaults).GetProperties().Where(property => property.PropertyType == typeof(SettingDefault));

    [GeneratedRegex(@"^\{x:Bind (?<source>\w+)\.(?<member>\w+)(,.*)?\}$")]
    private static partial Regex BindingPattern();
}
