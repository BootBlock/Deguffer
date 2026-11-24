using System.Windows.Input;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// What a list or a picture shows in place of its contents when it has none. A blank list reads as
/// a screen that has failed, so each says what is, or is not, there.
/// </summary>
public sealed partial class EmptyState
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(EmptyState),
        new PropertyMetadata(string.Empty, (state, e) => ((EmptyState)state).GlyphIcon.Visibility = ShownWhenSaid(e.NewValue)));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(EmptyState),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(EmptyState),
        new PropertyMetadata(string.Empty, (state, e) => ((EmptyState)state).MessageText.Visibility = ShownWhenSaid(e.NewValue)));

    public static readonly DependencyProperty ActionLabelProperty = DependencyProperty.Register(
        nameof(ActionLabel),
        typeof(string),
        typeof(EmptyState),
        new PropertyMetadata(string.Empty, (state, e) => ((EmptyState)state).ActionButton.Visibility = ShownWhenSaid(e.NewValue)));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand),
        typeof(ICommand),
        typeof(EmptyState),
        new PropertyMetadata(null));

    public EmptyState()
    {
        InitializeComponent();
        GlyphIcon.Visibility = Visibility.Collapsed;
        MessageText.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The Segoe Fluent Icons glyph above the title, left out where it is empty, as it is where the
    /// banner sits in a flyout too small to carry one.
    /// </summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The sentence under the title, left out where it is empty.</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>
    /// The words of the button under the sentence, which undoes what keeps the rows back, such as a
    /// filter. The button is left out where they are empty, so a list empty of anything to show
    /// offers nothing to press.
    /// </summary>
    public string ActionLabel
    {
        get => (string)GetValue(ActionLabelProperty);
        set => SetValue(ActionLabelProperty, value);
    }

    /// <summary>What the button does. See <see cref="ActionLabel"/>.</summary>
    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    private static Visibility ShownWhenSaid(object? text) =>
        string.IsNullOrEmpty((string?)text) ? Visibility.Collapsed : Visibility.Visible;
}
