using System.Text.Json.Serialization;

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
/// <param name="Strength">
/// How bright it is where it is shown. Soft by default: the light is there to show which shape the
/// pointer is on, and at Standard it competed with the colours it lies over.
/// </param>
/// <param name="Reach">How far from the pointer it falls: over the shape under it, or over its neighbours too.</param>
public sealed record MapPointerLight(
    bool Shown = true,
    MapLightStrength Strength = MapLightStrength.Soft,
    MapLightReach Reach = MapLightReach.Standard)
{
    /// <summary>The light a map has until the reader chooses another.</summary>
    public static readonly MapPointerLight Default = new();

    /// <summary>
    /// How much the light adds, at most, to a surface facing it straight on, as a fraction of the
    /// colour, or zero where it is not shown. Standard is the strength the map had before Soft became
    /// the default: enough to see, and short of washing a pale colour out to white.
    ///
    /// <para>Kept out of the file: a value derived from the two beside it would read, to anyone editing
    /// the file by hand, as a third thing they can set.</para>
    /// </summary>
    [JsonIgnore]
    public float Amount => !Shown ? 0 : Strength switch
    {
        MapLightStrength.Soft => 0.2f,
        MapLightStrength.Bright => 0.55f,
        _ => Exploring.Rendering.PointerLight.StandardStrength,
    };

    /// <summary>
    /// How far the light falls, as a multiple of <see cref="Exploring.Rendering.PointerLight.ReachInDips"/>,
    /// which is the reach it has always had. Kept out of the file, as <see cref="Amount"/> is.
    /// </summary>
    [JsonIgnore]
    public float Spread => Reach switch
    {
        MapLightReach.Narrow => 0.5f,
        MapLightReach.Wide => 1.75f,
        _ => 1f,
    };

    /// <summary>
    /// This light with a strength and a reach no member names read as the default's. The preferences
    /// file is read with numbers allowed for an enum, as <see cref="MapStyle.Defined"/> says.
    /// </summary>
    public MapPointerLight Defined() => this with
    {
        Strength = StoredEnum.Defined(Strength, Default.Strength),
        Reach = StoredEnum.Defined(Reach, Default.Reach),
    };
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

/// <summary>How far the pointer's light falls. Ordinal, as <see cref="MapLightStrength"/> is.</summary>
public enum MapLightReach
{
    /// <summary>Half the standard reach: little more than the shape under the pointer.</summary>
    Narrow = 0,

    /// <summary>The reach the light has always had, over a few shapes of a crowded picture.</summary>
    Standard = 1,

    /// <summary>Nearly twice the standard reach, a broad pool of light.</summary>
    Wide = 2,
}
