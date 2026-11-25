using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// No control sits inside an <c>Expander</c>'s header. The Expander makes its whole header one toggle
/// button, so a checkbox, a link or a button inside it is interactive content inside a button (WCAG
/// 4.1.2): a click in the gaps between them opens the disclosure, and a screen reader can report the
/// header as one button holding others. A line that holds controls and opens detail ends in a
/// <c>DisclosureButton</c> beside them instead. The rule has no exceptions.
///
/// <para>A header is everything the Expander draws there: its <c>Expander.Header</c>, its header
/// template, written inline or named as a resource, and every template and shell control those draw
/// in turn. A header that holds only a presenter of a template hides the template's controls from a
/// reading of the header's own markup.</para>
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

    /// <summary>The attributes through which an element draws a template keyed in the resources.</summary>
    private static readonly string[] TemplateAttributes = ["ContentTemplate", "ItemTemplate", "HeaderTemplate"];

    [Fact]
    public void NoControlSitsInsideADisclosureHeader()
    {
        var found = ShellMarkup.Files.SelectMany(file => Expanders(file)
            .SelectMany(expander => Header(file, expander)
                .Where(IsOperable)
                .Select(control => file.At(expander, $"<{control.Name.LocalName}> (line {ShellMarkup.LineOf(control)}) sits inside an Expander's header; end the line with a DisclosureButton beside it instead"))));

        RuleBaseline.Hold("no control sits inside a disclosure header", found, new Dictionary<string, int>());
    }

    /// <summary>
    /// Each way of giving an Expander its header is read, through the templates it draws. A sweep that
    /// read only the header's own markup would pass a header holding nothing but a presenter.
    /// </summary>
    [Theory]
    [InlineData("""<Expander><Expander.Header><ContentPresenter ContentTemplate="{StaticResource Row}" /></Expander.Header></Expander>""")]
    [InlineData("""<Expander HeaderTemplate="{StaticResource Row}" />""")]
    [InlineData("""<Expander><Expander.HeaderTemplate><DataTemplate><CheckBox /></DataTemplate></Expander.HeaderTemplate></Expander>""")]
    [InlineData("""<Expander><Expander.Header><ItemsControl ItemTemplate="{StaticResource Row}" /></Expander.Header></Expander>""")]
    public void EveryWayOfGivingAHeaderIsRead(string expander)
    {
        var file = Synthetic($"""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Page.Resources>
                    <DataTemplate x:Key="Row"><Grid><CheckBox /></Grid></DataTemplate>
                </Page.Resources>
                {expander}
            </Page>
            """);

        Assert.Contains(Header(file, Assert.Single(Expanders(file))), element => element.Name.LocalName == "CheckBox");
    }

    /// <summary>A template the application's resources hold is followed as one the file holds is.</summary>
    [Fact]
    public void AHeaderTemplateFromTheApplicationResourcesIsRead()
    {
        var application = Synthetic("""
            <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Application.Resources>
                    <DataTemplate x:Key="Row"><Grid><CheckBox /></Grid></DataTemplate>
                </Application.Resources>
            </Application>
            """);
        var file = Synthetic("""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Expander HeaderTemplate="{StaticResource Row}" />
            </Page>
            """);

        Assert.Contains(Header(file, Assert.Single(Expanders(file)), application), element => element.Name.LocalName == "CheckBox");
    }

    // A sweep that stopped finding the headers would pass the rule with nothing to check, so it must
    // find at least the Expanders the shell draws with a header today.
    [Fact]
    public void TheSweepFindsTheDisclosureHeaders()
    {
        var count = ShellMarkup.Files.Sum(file => Expanders(file).Count(expander => Header(file, expander).Any()));

        Assert.True(count >= 4, $"Only {count} Expander headers were found, so the rule is no longer reading the shell.");
    }

    private static MarkupFile Application => Assert.Single(ShellMarkup.Files, file => file.IsApplicationResources);

    private static IEnumerable<XElement> Expanders(MarkupFile file) =>
        file.Elements.Where(element => element.Name.LocalName == "Expander");

    /// <summary>Everything <paramref name="expander"/> draws in its header, however the header is given.</summary>
    private static IEnumerable<XElement> Header(MarkupFile file, XElement expander) => Header(file, expander, Application);

    private static IEnumerable<XElement> Header(MarkupFile file, XElement expander, MarkupFile application)
    {
        var seen = new HashSet<XElement>();

        var given = expander.Elements()
            .Where(element => element.Name.LocalName is "Expander.Header" or "Expander.HeaderTemplate")
            .SelectMany(element => Inside(file, element, application, seen));

        var named = expander.Attribute("HeaderTemplate")?.Value is { } reference && TemplateNamed(file, application, reference) is { } template
            ? Inside(file, template, application, seen)
            : [];

        return [.. given, .. named];
    }

    private static bool IsOperable(XElement element) =>
        ShellMarkup.IsPressable(element) || Operable.Contains(element.Name.LocalName);

    /// <summary>
    /// Everything drawn inside <paramref name="root"/>: its own descendants, the descendants of any
    /// template it names from the resources, and those of any of the shell's own controls it places,
    /// followed as far as they go.
    /// </summary>
    private static IEnumerable<XElement> Inside(MarkupFile file, XElement root, MarkupFile application, HashSet<XElement> seen)
    {
        foreach (var element in root.Descendants())
        {
            if (!seen.Add(element))
            {
                continue;
            }

            yield return element;

            foreach (var reference in TemplateAttributes.Select(name => element.Attribute(name)?.Value).OfType<string>())
            {
                if (TemplateNamed(file, application, reference) is { } template)
                {
                    foreach (var drawn in Inside(file, template, application, seen))
                    {
                        yield return drawn;
                    }
                }
            }

            if (ControlFile(element) is { } control && control.Document.Root is { } controlRoot)
            {
                foreach (var drawn in Inside(control, controlRoot, application, seen))
                {
                    yield return drawn;
                }
            }
        }
    }

    /// <summary>
    /// The template a <c>{StaticResource …}</c> reference names, looked up as the framework looks it
    /// up for a page: in the file's own resources first, then in the application's.
    /// </summary>
    private static XElement? TemplateNamed(MarkupFile file, MarkupFile application, string reference)
    {
        if (!reference.StartsWith("{StaticResource ", StringComparison.Ordinal))
        {
            return null;
        }

        var key = reference["{StaticResource ".Length..].TrimEnd('}').Trim();

        return KeyedTemplate(file, key) ?? KeyedTemplate(application, key);
    }

    private static XElement? KeyedTemplate(MarkupFile file, string key) => file.Elements.FirstOrDefault(candidate =>
        candidate.Name.LocalName == "DataTemplate" && candidate.Attribute(Xaml + "Key")?.Value == key);

    /// <summary>The XAML file whose class <paramref name="element"/> is, where it is one of the shell's own controls.</summary>
    private static MarkupFile? ControlFile(XElement element) =>
        ShellMarkup.Files.FirstOrDefault(file =>
            file.Document.Root?.Attribute(Xaml + "Class")?.Value is { } type
            && type.EndsWith($".{element.Name.LocalName}", StringComparison.Ordinal));

    private static MarkupFile Synthetic(string markup) =>
        new("Synthetic.xaml", XDocument.Parse(markup, LoadOptions.SetLineInfo), markup.Split('\n').Length);
}
