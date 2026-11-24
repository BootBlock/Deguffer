using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Controls;

/// <summary>
/// The rest of an explanation, one activation away: a settings row's detail, or the part of a
/// page's introduction past its first sentence.
/// </summary>
public sealed partial class ExplanationDisclosure : UserControl
{
    public static readonly DependencyProperty SubjectProperty =
        DependencyProperty.Register(nameof(Subject), typeof(string), typeof(ExplanationDisclosure), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(ExplanationDisclosure), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<string>), typeof(ExplanationDisclosure), new PropertyMetadata(null));

    public ExplanationDisclosure() => InitializeComponent();

    /// <summary>
    /// What the explanation is about, which names the disclosure to a screen reader. Every one reads
    /// "More about this" on screen, so a reader moving down the page would otherwise hear the same
    /// words on every row with nothing to tell one from another. The name keeps those words first,
    /// so a speech user who says what the screen shows still reaches it.
    /// </summary>
    public string Subject
    {
        get => (string)GetValue(SubjectProperty);
        set => SetValue(SubjectProperty, value);
    }

    /// <summary>The explanation, as a paragraph.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The names the explanation lists after its paragraph, or none.</summary>
    public IReadOnlyList<string>? Items
    {
        get => (IReadOnlyList<string>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>The disclosure's accessible name, which starts with the words it shows.</summary>
    public static string NameFor(string subject) => $"More about this: {subject}";

    /// <summary>Whether there is a list to draw under the paragraph.</summary>
    public static Visibility IsListed(IReadOnlyList<string>? items) =>
        items is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
}
