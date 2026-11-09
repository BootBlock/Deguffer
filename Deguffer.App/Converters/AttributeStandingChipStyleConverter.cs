using Deguffer.Core.Health;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Converters;

/// <summary>
/// A drive reading's standing, as a <see cref="Style"/> for <see cref="TierChipStyleConverter"/>'s
/// reason, in the colours a drive's verdict uses. The chip's word states the standing, so the colour
/// only reinforces it (§6.5).
/// </summary>
public sealed partial class AttributeStandingChipStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Style)Application.Current.Resources[value switch
        {
            AttributeStanding.Watch => "TierChipRegenerableWithCost",
            AttributeStanding.Failing => "TierChipUserData",
            _ => "TierChipRegenerableCache",
        }];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("A standing's colour is display-only.");
}
