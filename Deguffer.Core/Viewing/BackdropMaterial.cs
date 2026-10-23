namespace Deguffer.Core.Viewing;

/// <summary>
/// The material a window is drawn on (§6.5). Decoration only: the window reads the same on
/// <see cref="Solid"/>, which is its own ground with nothing behind it.
/// </summary>
public enum BackdropMaterial
{
    /// <summary>No material: the window's own solid ground.</summary>
    Solid,

    /// <summary>The glass-like Acrylic §6.5 decides on.</summary>
    Acrylic,

    /// <summary>Mica, where the system cannot draw Acrylic but can draw Mica.</summary>
    Mica,
}

/// <summary>Which material a window is drawn on, from what the reader asked for and what the system can draw.</summary>
public static class BackdropChoice
{
    /// <summary>
    /// The material for a window whose reader <paramref name="requested"/> one, or not.
    ///
    /// <para>High contrast wins over the request. §6.5 makes the backdrop decoration, and no preference
    /// may put translucency back over a stated accessibility requirement. Otherwise Acrylic, the look
    /// §6.5 decides on, and Mica where the system cannot draw Acrylic, so a window keeps a material
    /// rather than falling straight to its ground.</para>
    /// </summary>
    /// <param name="acrylicSupported">Whether the system can draw Acrylic in this process.</param>
    /// <param name="micaSupported">Whether the system can draw Mica in this process.</param>
    public static BackdropMaterial For(bool requested, bool highContrast, bool acrylicSupported, bool micaSupported) =>
        !requested || highContrast ? BackdropMaterial.Solid
        : acrylicSupported ? BackdropMaterial.Acrylic
        : micaSupported ? BackdropMaterial.Mica
        : BackdropMaterial.Solid;
}
