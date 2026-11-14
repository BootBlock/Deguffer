using Deguffer.Core.Startup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Converters;

/// <summary>
/// A Startup row's tint, as a <see cref="Style"/> for <see cref="StandingChipStyleConverter"/>'s reason:
/// green where the entry starts and red where it is disabled, so the two read apart down the list. An
/// entry whose state Windows' record would not give is left untinted, because a colour would claim an
/// answer the badge does not.
/// </summary>
public sealed partial class StartupRowStyleConverter : IValueConverter
{
    /// <summary>The <c>App.xaml</c> style that tints a row in <paramref name="state"/>.</summary>
    public static string KeyFor(StartupState state) => state switch
    {
        StartupState.Starts => "StartupRowStarts",
        StartupState.Disabled => "StartupRowDisabled",
        _ => "StartupRowUnknown",
    };

    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Style)Application.Current.Resources[KeyFor((StartupState)value)];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("A startup row's tint is display-only.");
}
