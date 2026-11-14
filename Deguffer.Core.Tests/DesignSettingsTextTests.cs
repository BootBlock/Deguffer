namespace Deguffer.Core.Tests;

/// <summary>
/// A setting's always-visible description is one short line: what the setting does, and what it
/// costs where that changes the decision. The rest of the explanation goes behind a disclosure the
/// keyboard and a screen reader can open. A page of 800-character paragraphs in small grey text is
/// one users stop reading, and then they also skip the sentence that matters.
///
/// <para>Fluent's settings card pattern (a header and a one-line description, with detail in an
/// expander).</para>
/// </summary>
public sealed class DesignSettingsTextTests
{
    private const int DescriptionBudget = 140;

    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/SettingsPage.xaml"] = 18,
    };

    [Fact]
    public void EverySettingIsDescribedInOneShortLine()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => element.Name.LocalName == "SettingsRow"
                && ShellMarkup.Literal(element, "Description") is { Length: > DescriptionBudget })
            .Select(row => file.At(row, $"'{row.Attribute("Title")?.Value}' is described in {row.Attribute("Description")!.Value.Length} characters, over {DescriptionBudget}")));

        RuleBaseline.Hold($"a settings row's visible description is at most {DescriptionBudget} characters", found, Baseline);
    }
}
