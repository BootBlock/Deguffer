using Deguffer.App.Shell;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The colours <see cref="CleanSpotlight"/> lights a row in, for the system's look and the page's theme.
/// </summary>
/// <param name="Mark">The frame, the edge and the rail: the accent, or the high contrast highlight.</param>
/// <param name="Shine">The band that sweeps across the row.</param>
/// <param name="Contrast">
/// Whether Windows is in a high contrast theme. Then only the frame and the rail are drawn, solid, in the
/// theme's highlight colour: a tint laid over the row's text would change the contrast the reader chose.
/// </param>
internal readonly record struct CleanSpotlightLook(Color Mark, Color Shine, bool Contrast)
{
    /// <summary>
    /// The look for <paramref name="state"/> on a page in <paramref name="theme"/>. A dark page takes the
    /// lightest accent and a white shine. A light page takes the darker accent for both, because white and
    /// the pale accent vanish against it.
    /// </summary>
    public static CleanSpotlightLook For(SystemLookState state, ElementTheme theme) =>
        state.Contrast is { } contrast
            ? new(contrast.Highlight, contrast.Highlight, Contrast: true)
            : theme == ElementTheme.Light
                ? new(state.AccentDark, state.AccentDark, Contrast: false)
                : new(state.Accent, Colors.White, Contrast: false);
}
