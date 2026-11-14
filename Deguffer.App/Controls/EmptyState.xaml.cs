using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// What a list shows in place of its rows when it has none. A blank list reads as a screen that has
/// failed, so each says what is, or is not, there.
/// </summary>
public sealed partial class EmptyState
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(EmptyState),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(EmptyState),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(EmptyState),
        new PropertyMetadata(string.Empty, (state, e) => ((EmptyState)state).MessageText.Visibility =
            string.IsNullOrEmpty((string?)e.NewValue) ? Visibility.Collapsed : Visibility.Visible));

    public EmptyState()
    {
        InitializeComponent();
        MessageText.Visibility = Visibility.Collapsed;
    }

    /// <summary>The Segoe Fluent Icons glyph above the title.</summary>
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
}
