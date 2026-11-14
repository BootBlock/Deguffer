using Windows.UI;

namespace Deguffer.App.Shell;

/// <summary>What the system's settings say the app should look like, read at one moment.</summary>
/// <param name="Contrast">The high contrast theme Windows is in, or null where it is in none.</param>
/// <param name="Accent">The lightest of the accent's shades the app draws with (<c>AccentLight2</c>).</param>
/// <param name="AccentDark">
/// The accent's first darker shade (<c>AccentDark1</c>), for a mark that must stand out on a light theme,
/// where the lightest shade is pale against the page.
/// </param>
/// <param name="TextScale">The reader's Windows text size, as a factor of the standard size.</param>
/// <param name="EffectsFast">Whether the compositor can draw effects without slowing the picture down.</param>
internal readonly record struct SystemLookState(ContrastTheme? Contrast, Color Accent, Color AccentDark, double TextScale, bool EffectsFast)
{
    /// <summary>Which parts differ in <paramref name="next"/>, or <see cref="SystemLookChange.None"/>.</summary>
    public SystemLookChange ChangesTo(SystemLookState next)
    {
        var change = SystemLookChange.None;

        if (next.Contrast != Contrast || next.Accent != Accent || next.AccentDark != AccentDark)
        {
            change |= SystemLookChange.Colours;
        }

        if (next.TextScale != TextScale)
        {
            change |= SystemLookChange.TextScale;
        }

        if (next.EffectsFast != EffectsFast)
        {
            change |= SystemLookChange.Effects;
        }

        return change;
    }
}
