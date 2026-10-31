using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Rendering;
using Windows.UI;

namespace Deguffer.App.Shell;

/// <summary>
/// The colours of the high contrast theme Windows is in, read from the system rather than the
/// application's resources, which answer for the theme the app was started in.
/// </summary>
/// <param name="Window">The theme's window colour.</param>
/// <param name="Text">The theme's text colour on that window.</param>
/// <param name="Highlight">The theme's colour for what is selected.</param>
/// <param name="Hotlight">The theme's colour for what the pointer is over.</param>
internal readonly record struct ContrastTheme(Color Window, Color Text, Color Highlight, Color Hotlight)
{
    /// <summary>The two of these the map's picture is drawn in. See <see cref="ContrastColours"/>.</summary>
    public ContrastColours ToMap() => new(Window.ToTile(), Text.ToTile());

    /// <summary>The ones the outlines over the map are drawn in.</summary>
    public SystemHighlight ToHighlight() => new(Window, Highlight, Hotlight);
}
