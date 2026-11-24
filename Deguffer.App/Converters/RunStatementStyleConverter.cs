using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Converters;

/// <summary>
/// How the Memory page states what a close did: in the critical colour when its §5.6 check failed,
/// and quiet otherwise. A clean's verdict has a heading and a glyph of its own
/// (<see cref="SafetyCheckHeadingStyleConverter"/>).
///
/// Quiet covers every other outcome, and deliberately. A process that exited on its own is a fact the
/// report states in words and lists underneath, and not a fault to report. Colouring it as one would
/// spend the critical colour on ordinary events and leave nothing to mark the real thing with.
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
            value is true ? "CriticalStatement" : "SecondaryText"];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("Verdict colour is display-only.");
}
