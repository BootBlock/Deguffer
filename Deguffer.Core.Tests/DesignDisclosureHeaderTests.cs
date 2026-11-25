using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// No control sits inside an <c>Expander</c>'s header. The Expander makes its whole header one toggle
/// button, so a checkbox, a link or a button inside it is interactive content inside a button (WCAG
/// 4.1.2): a click in the gaps between them opens the disclosure, and a screen reader can report the
/// header as one button holding others. A line that holds controls and opens detail ends in a
/// <c>DisclosureButton</c> beside them instead. The compact Storage row and the group of rows waiting
/// for a source folder both broke this when it was written, and both were rebuilt, so it holds with
/// no exceptions.
///
/// <para>A header that draws a template or one of the shell's own controls is read through it, which
/// is how the compact row hid its checkbox: the header held only a presenter of the row's template.</para>
/// </summary>
public sealed class DesignDisclosureHeaderTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>What a user operates, beyond the buttons <see cref="ShellMarkup.Pressable"/> names.</summary>
    private static readonly IReadOnlySet<string> Operable = new HashSet<string>(StringComparer.Ordinal)
    {
        "CheckBox", "RadioButton", "ToggleSwitch", "ComboBox", "TextBox", "NumberBox", "PasswordBox",
        "AutoSuggestBox", "Slider", "RatingControl", "Hyperlink", "CalendarDatePicker", "DatePicker",
        "TimePicker",
    };

    [Fact]
    public void NoControlSitsInsideADisclosureHeader()
    {
        var found = ShellMarkup.Files.SelectMany(file => Headers(file)
            .SelectMany(header => Inside(file, header, [])
                .Where(IsOperable)
                .Select(control => file.At(header, $"<{control.Name.LocalName}> (line {ShellMarkup.LineOf(control)}) sits inside an Expander's header; end the line with a DisclosureButton beside it instead"))));

        RuleBaseline.Hold("no control sits inside a disclosure header", found, new Dictionary<string, int>());
    }

    /// <summary>
    /// The rule reads through a header's template, and one that stopped doing so would pass the rule
    /// above for the very header it was written against. A header built as the compact row's was
    /// must be found holding its checkbox.
    /// </summary>
    [Fact]
    public void AHeaderIsReadThroughTheTemplateItDraws()
    {
        var file = Synthetic("""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Page.Resources>
                    <DataTemplate x:Key="Row"><Grid><CheckBox /></Grid></DataTemplate>
                </Page.Resources>
                <Expander>
                    <Expander.Header>
                        <ContentPresenter ContentTemplate="{StaticResource Row}" />
                    </Expander.Header>
                </Expander>
            </Page>
            """);

        var header = Assert.Single(Headers(file));

        Assert.Contains(Inside(file, header, []), element => element.Name.LocalName == "CheckBox");
    }

    // A sweep that stopped finding the headers would pass the rule with nothing to check. The shell
    // had four Expanders with a header when this was written.
    [Fact]
    public void TheSweepFindsTheDisclosureHeaders()
    {
        var count = ShellMarkup.Files.Sum(file => Headers(file).Count());

        Assert.True(count >= 4, $"Only {count} Expander headers were found, so the rule is no longer reading the shell.");
    }

    private static IEnumerable<XElement> Headers(MarkupFile file) =>
        file.Elements.Where(element => element.Name.LocalName == "Expander.Header");

    private static bool IsOperable(XElement element) =>
        ShellMarkup.IsPressable(element) || Operable.Contains(element.Name.LocalName);

    /// <summary>
    /// Everything drawn inside <paramref name="root"/>: its own descendants, the descendants of any
    /// template it names from its file's resources, and those of any of the shell's own controls it
    /// places, followed as far as they go.
    /// </summary>
    private static IEnumerable<XElement> Inside(MarkupFile file, XElement root, HashSet<XElement> seen)
    {
        foreach (var element in root.Descendants())
        {
            if (!seen.Add(element))
            {
                continue;
            }

            yield return element;

            if (TemplateNamed(file, element) is { } template)
            {
                foreach (var drawn in Inside(file, template, seen))
                {
                    yield return drawn;
                }
            }

            if (ControlFile(element) is { } control)
            {
                foreach (var drawn in Inside(control, control.Document.Root!, seen))
                {
                    yield return drawn;
                }
            }
        }
    }

    private static XElement? TemplateNamed(MarkupFile file, XElement element)
    {
        if (element.Attribute("ContentTemplate")?.Value is not { } reference
            || !reference.StartsWith("{StaticResource ", StringComparison.Ordinal))
        {
            return null;
        }

        var key = reference["{StaticResource ".Length..].TrimEnd('}').Trim();

        return file.Elements.FirstOrDefault(candidate =>
            candidate.Name.LocalName == "DataTemplate" && candidate.Attribute(Xaml + "Key")?.Value == key);
    }

    /// <summary>The XAML file whose class <paramref name="element"/> is, where it is one of the shell's own controls.</summary>
    private static MarkupFile? ControlFile(XElement element) =>
        ShellMarkup.Files.FirstOrDefault(file =>
            file.Document.Root?.Attribute(Xaml + "Class")?.Value is { } type
            && type.EndsWith($".{element.Name.LocalName}", StringComparison.Ordinal));

    private static MarkupFile Synthetic(string markup) =>
        new("Synthetic.xaml", XDocument.Parse(markup, LoadOptions.SetLineInfo), markup.Split('\n').Length);
}
