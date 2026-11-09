using Deguffer.Core.Health;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Converters;

/// <summary>
/// A drive's verdict chip, as a <see cref="Style"/> for <see cref="TierChipStyleConverter"/>'s reason:
/// a brush resolved here would keep its colour over a theme change. The chip's word states the
/// verdict, so the colour only reinforces it (§6.5).
/// </summary>
public sealed partial class DriveVerdictChipStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Style)Application.Current.Resources[value switch
        {
            DriveVerdict.Good => "TierChipRegenerableCache",
            DriveVerdict.Watch => "TierChipRegenerableWithCost",
            DriveVerdict.Failing => "TierChipUserData",
            _ => "StandingChip",
        }];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("A verdict's colour is display-only.");
}
