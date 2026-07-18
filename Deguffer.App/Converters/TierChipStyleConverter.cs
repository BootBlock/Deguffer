using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Converters;

/// <summary>
/// §7's "colour by tier", as a <see cref="Style"/> rather than a brush.
///
/// Returning brushes here was wrong in a way that only showed up on a theme change: resolving
/// <c>Application.Current.Resources[key]</c> in C# snapshots whatever brush the *current* theme
/// maps, and nothing re-runs the converter afterwards, so chips kept their old colours over a
/// repainted window. A Style is theme-independent — the <c>ThemeResource</c> references inside its
/// setters are resolved by each element that applies it, and re-resolved when the theme changes.
///
/// The tier is also always stated in words beside the colour, because §6.5 requires the
/// classification to survive a flat background and a high-contrast theme.
/// </summary>
public sealed partial class TierChipStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value switch
        {
            SafetyTier.RegenerableCache => "TierChipRegenerableCache",
            SafetyTier.RegenerableWithCost => "TierChipRegenerableWithCost",
            SafetyTier.UserData => "TierChipUserData",

            // Tier 4 is excluded from the list entirely (§5.2), so this is only reached if one ever
            // leaks through. Neutral rather than an alarm colour: such a row would be a bug in the
            // provider, not a danger to the user, and it must still be readable.
            _ => "TierChipDoNotTouch",
        };

        return (Style)Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("Tier colour is display-only.");
}
