using Deguffer.App.Shell;
using Deguffer.Core.Viewing;
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

    public static readonly DependencyProperty ControlProperty =
        DependencyProperty.Register(nameof(Control), typeof(object), typeof(SettingsRow), new PropertyMetadata(null));

    public static readonly DependencyProperty SaveFailedProperty =
        DependencyProperty.Register(nameof(SaveFailed), typeof(bool), typeof(SettingsRow), new PropertyMetadata(false, OnSaveFailedChanged));

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
    /// Whether the last change made through this row could not be written to disk. The row says so
    /// under its words, and the page scrolls to the notice, so it is read where the value went back.
    /// </summary>
    public bool SaveFailed
    {
        get => (bool)GetValue(SaveFailedProperty);
        set => SetValue(SaveFailedProperty, value);
    }

    private static void OnSaveFailedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs change) =>
        ((SettingsRow)sender).ShowSaveNotice((bool)change.NewValue);

    private void ShowSaveNotice(bool failed)
    {
        SaveNotice.IsOpen = failed;

        if (!failed)
        {
            return;
        }

        // Laid out first: while it was closed the notice took no room, so the scroller would be asked
        // to show where it was rather than where it now is, and could stop short of its last line.
        UpdateLayout();

        SaveNotice.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = SystemMotion.Current.For(MotionToken.List).Travels,
        });
    }

    /// <summary>Whether a part of the row with words in it, its details or its unit, is drawn.</summary>
    public static Visibility IsShown(string words) =>
        string.IsNullOrEmpty(words) ? Visibility.Collapsed : Visibility.Visible;
}
