using Deguffer.Core.Startup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Converters;

/// <summary>
/// A startup entry's badge, as a <see cref="Style"/> for <see cref="StandingChipStyleConverter"/>'s
/// reason. Neutral whether the entry starts or not, because neither is a warning (§7.5 recommends
/// nothing), and cautionary only where Windows' record could not be read.
/// </summary>
public sealed partial class StartupChipStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Style)Application.Current.Resources[value is StartupState.Unknown ? "StandingChipUnproven" : "StandingChip"];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("A startup state's colour is display-only.");
}
