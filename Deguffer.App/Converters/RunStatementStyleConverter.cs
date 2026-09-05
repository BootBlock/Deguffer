using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Converters;

/// <summary>
/// How the run-result card states its §5.6 verdict: in the critical colour when a protected path
/// went missing where this run could have taken it, and quiet otherwise.
///
/// Quiet covers two verdicts rather than one, and deliberately. A path something else removed while
/// the preview sat on screen is a fact the card states in words and lists underneath, and it is not
/// a fault to report — colouring it as one would spend the critical colour on the ordinary event and
/// leave nothing to mark the real thing with.
///
/// A <see cref="Style"/> rather than a brush, for the reason <see cref="TierChipStyleConverter"/>
/// gives — resolving <c>Application.Current.Resources[key]</c> in C# snapshots the theme in force at
/// the time, and nothing re-runs a converter afterwards, so the text would keep its old colour over
/// a repainted window.
///
/// The verdict is always stated in words as well, because §6.5 requires it to survive a flat
/// background and a high-contrast theme.
/// </summary>
public sealed partial class RunStatementStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Style)Application.Current.Resources[
            value is true ? "CardStatementCritical" : "CardStatement"];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("Verdict colour is display-only.");
}
