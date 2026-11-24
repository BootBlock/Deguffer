using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// One labelled setting: icon, title, explanation, and the control that changes it.
///
/// It exists because the Settings page repeats that shape for every entry, and spelling the grid
/// out four times is how the rows drift out of alignment with each other. The description is not
/// optional — a setting nobody can explain in a sentence is a setting that should not be there.
///
/// <para>The description is that sentence, kept to one short line: what the setting does, and what
/// it costs where that changes the decision. Anything more is <see cref="Details"/>, one activation away, because a
/// page of paragraphs at caption size is one a reader stops reading, and then misses the sentence
/// that mattered. <c>DesignSettingsTextTests</c> holds the description to its budget.</para>
/// </summary>
public sealed partial class SettingsRow : UserControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DetailsProperty =
        DependencyProperty.Register(nameof(Details), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DetailItemsProperty =
        DependencyProperty.Register(nameof(DetailItems), typeof(IReadOnlyList<string>), typeof(SettingsRow), new PropertyMetadata(null));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DefaultProperty =
        DependencyProperty.Register(nameof(Default), typeof(SettingReset), typeof(SettingsRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ControlProperty =
        DependencyProperty.Register(nameof(Control), typeof(object), typeof(SettingsRow), new PropertyMetadata(null));

    public SettingsRow() => InitializeComponent();

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

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>The rest of the explanation, behind the row's disclosure. Empty draws no disclosure.</summary>
    public string Details
    {
        get => (string)GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }

    /// <summary>Names the explanation lists after <see cref="Details"/>, or none.</summary>
    public IReadOnlyList<string>? DetailItems
    {
        get => (IReadOnlyList<string>?)GetValue(DetailItemsProperty);
        set => SetValue(DetailItemsProperty, value);
    }

    /// <summary>
    /// The word after the control, for a number box: "hours" after 8. Empty draws none. Bound through
    /// <see cref="Deguffer.Core.Text.SettingUnit"/> where it agrees with the number.
    /// </summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public object? Control
    {
        get => GetValue(ControlProperty);
        set => SetValue(ControlProperty, value);
    }

    /// <summary>
    /// The setting's default, for a row that sets a value: the row states it under the description and
    /// offers a reset while the value is off it. Null draws neither, for a row that lists or acts.
    /// </summary>
    public SettingReset? Default
    {
        get => (SettingReset?)GetValue(DefaultProperty);
        set => SetValue(DefaultProperty, value);
    }

    /// <summary>Whether the row has a default to state.</summary>
    public static Visibility IsStated(SettingReset? setting) => setting is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The reset's accessible name, which starts with the word it shows: <c>Reset Theme</c>.</summary>
    public static string ResetName(string title) => $"Reset {title}";

    /// <summary>
    /// The reset's automation id, from the row's title, as every row has its own and the words of a
    /// title are its name: <c>ResetLeaveRecentlyChangedFilesAlone</c>.
    /// </summary>
    public static string ResetId(string title) =>
        string.Concat(ResetName(title).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    private void OnResetClicked(object sender, RoutedEventArgs e) => Default?.Reset();

    /// <summary>Whether a part of the row with words in it, its details or its unit, is drawn.</summary>
    public static Visibility IsShown(string words) =>
        string.IsNullOrEmpty(words) ? Visibility.Collapsed : Visibility.Visible;
}
