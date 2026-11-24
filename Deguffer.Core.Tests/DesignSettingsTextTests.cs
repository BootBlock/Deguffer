using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// A setting's always-visible description is one short line: what the setting does, and what it
/// costs where that changes the decision. The rest of the explanation goes behind a disclosure the
/// keyboard and a screen reader can open. A page of 800-character paragraphs in small grey text is
/// one users stop reading, and then they also skip the sentence that matters. A page's introduction
/// is held to the same line, for the same reason, and has the same disclosure for the rest.
///
/// <para>Fluent's settings card pattern (a header and a one-line description, with detail in an
/// expander). A description bound to a view-model is held to the budget by that view-model's own
/// test, since the markup cannot say what it reads.</para>
/// </summary>
public sealed class DesignSettingsTextTests
{
    private const int DescriptionBudget = 140;

    /// <summary>Each element that shows a sentence in view, and the attribute holding it.</summary>
    private static readonly (string Element, string Attribute)[] SentencesInView =
    [
        ("SettingsRow", "Description"),
        ("PageIntroduction", "Text"),
    ];

    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal);

    [Fact]
    public void EverySettingAndPageIsDescribedInOneShortLine()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .SelectMany(element => SentencesInView
                .Where(shown => element.Name.LocalName == shown.Element)
                .Select(shown => (Element: element, Text: ShellMarkup.Literal(element, shown.Attribute))))
            .Where(shown => shown.Text is { Length: > DescriptionBudget })
            .Select(shown => file.At(shown.Element, $"'{Subject(shown.Element)}' is described in {shown.Text!.Length} characters, over {DescriptionBudget}")));

        RuleBaseline.Hold($"a settings row's or a page introduction's visible sentence is at most {DescriptionBudget} characters", found, Baseline);
    }

    private static string? Subject(XElement element) =>
        element.Attribute("Title")?.Value ?? element.Attribute("Destination")?.Value;
}
