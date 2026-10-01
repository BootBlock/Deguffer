using Deguffer.Core.InstalledApps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Converters;

/// <summary>
/// An installed app's standing badge, as a <see cref="Style"/> for <see cref="TierChipStyleConverter"/>'s
/// reason: a brush resolved here would keep its colour over a theme change. The badge's word says
/// the standing, so the colour only reinforces it (§6.5).
/// </summary>
public sealed partial class StandingChipStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Style)Application.Current.Resources[value is EntryStanding.Unproven ? "StandingChipUnproven" : "StandingChip"];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("A standing's colour is display-only.");
}
