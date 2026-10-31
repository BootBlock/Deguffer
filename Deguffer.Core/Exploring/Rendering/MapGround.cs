namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The colour a map's canvas stands on, taken from the theme rather than fixed.
///
/// <para>§6.5 requires the UI to read correctly on a flat background in either theme, and this is
/// where the reference implementation took the shortcut this cannot: WinDirStat's newer views
/// hard-code a near-black ground and are dark whatever the system is set to.</para>
/// </summary>
public static class MapGround
{
    /// <summary>The ground under a map in the dark theme, the colour of the window's own layer.</summary>
    private static readonly TileColour Dark = new(32, 32, 32);

    /// <summary>The ground under a map in the light theme, the colour of the window's own layer.</summary>
    private static readonly TileColour Light = new(243, 243, 243);

    /// <summary>
    /// The ground for a map drawn in the dark theme or the light one, or in a high contrast theme's
    /// <paramref name="contrast"/> where there is one: that theme's own window colour, which the shapes
    /// are filled with too, whatever the app's own theme.
    /// </summary>
    public static TileColour For(bool dark, ContrastColours? contrast) => contrast is { } colours
        ? colours.Window
        : dark ? Dark : Light;
}
