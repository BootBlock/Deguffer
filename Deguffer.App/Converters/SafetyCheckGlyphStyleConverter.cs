using Deguffer.Core.Execution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Converters;

/// <summary>
/// The colour of a clean's safety check glyph: success for a pass, caution for a check that could not
/// be completed, and critical for a failure. The glyph's shape and the heading's words say the same
/// (<see cref="ViewModels.SafetyCheckGlyph"/>), so the colour backs them up and never stands alone.
///
/// <para>A <see cref="Style"/> rather than a brush, for the reason <see cref="TierChipStyleConverter"/>
/// gives: a brush resolved in C# keeps the theme in force when it was resolved, and nothing re-runs a
/// converter when the theme changes.</para>
/// </summary>
public sealed partial class SafetyCheckGlyphStyleConverter : IValueConverter
{
    /// <summary>
    /// The <c>App.xaml</c> style for the glyph beside <paramref name="verdict"/>. An unnamed verdict
    /// throws rather than taking the success colour.
    /// </summary>
    public static string KeyFor(RunVerdict verdict) => verdict switch
    {
        RunVerdict.AllSurvived => "SafetyCheckPassedGlyph",
        RunVerdict.Unverified or RunVerdict.RemovedFromOutside => "SafetyCheckIncompleteGlyph",
        RunVerdict.VerificationFailed => "SafetyCheckFailedGlyph",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Style)Application.Current.Resources[KeyFor((RunVerdict)value)];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("A safety check's colour is display-only.");
}
