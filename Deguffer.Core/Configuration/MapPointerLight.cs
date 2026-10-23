namespace Deguffer.Core.Configuration;

/// <summary>
/// The soft light the map shines over the point under the pointer (see
/// <see cref="Exploring.Rendering.PointerLight"/>): whether it is shown, and how bright it is.
///
/// <para>Apart from <see cref="MapStyle"/>, because it is how the map answers the pointer rather than
/// how its shapes are finished. A preset sets the finish as a whole, and a reader who turned the light
/// off has not asked for it back by choosing one.</para>
///
/// <para>Presentation only: it changes what a shape under the pointer looks like, never which shapes
/// there are or what may be removed. A group of its own in the preferences, as
/// <see cref="AppPreferences.MapStyle"/> is, so a file written before it existed reads with the light
/// the map always had.</para>
/// </summary>
/// <param name="Shown">Whether the light is shown at all.</param>
/// <param name="Strength">How bright it is where it is shown.</param>
public sealed record MapPointerLight(bool Shown = true, MapLightStrength Strength = MapLightStrength.Standard)
{
    /// <summary>The light the map had before it could be chosen.</summary>
    public static readonly MapPointerLight Default = new();

    /// <summary>
    /// How much the light adds, at most, to a surface facing it straight on, as a fraction of the
    /// colour, or zero where it is not shown. Standard is the strength the map always had: enough to
    /// see, and short of washing a pale colour out to white.
    /// </summary>
    public float Amount => !Shown ? 0 : Strength switch
    {
        MapLightStrength.Soft => 0.2f,
        MapLightStrength.Bright => 0.55f,
        _ => Exploring.Rendering.PointerLight.StandardStrength,
    };

    /// <summary>
    /// This light with a strength no member names read as the default's. The preferences file is read
    /// with numbers allowed for an enum, as <see cref="MapStyle.Defined"/> says.
    /// </summary>
    public MapPointerLight Defined() => this with { Strength = StoredEnum.Defined(Strength, Default.Strength) };
}

/// <summary>
/// How bright the pointer's light is. The values are ordinal, and the appearance window lists them in
/// this order. They are stored by name, as <see cref="MapShading"/> is.
/// </summary>
public enum MapLightStrength
{
    Soft = 0,
    Standard = 1,
    Bright = 2,
}
