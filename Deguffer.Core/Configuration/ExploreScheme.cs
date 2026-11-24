namespace Deguffer.Core.Configuration;

/// <summary>
/// A set of colours a map can be drawn in: how light and how saturated each branch is, and which
/// ramp the age bands run along. Chosen per view, because the three pictures put their colour in
/// different places — a treemap's in thousands of small tiles, a sunburst's in a few broad rings —
/// and a set that suits one can be too loud or too faint on another.
///
/// <para>Every set keeps the guarantees the standard one makes: a lightness step per level of
/// depth, a lifted step between neighbours, an age ramp that darkens at every step backwards, and a
/// grey for an undated entry that is on none of those ramps. The sets differ in how the picture
/// looks, never in what it says.</para>
///
/// <para>The values are ordinal, and a picker lists them in this order. They are stored by name, as
/// <see cref="ExploreView"/> is.</para>
/// </summary>
public enum ExploreScheme
{
    /// <summary>Medium lightness and saturation, with the age bands along viridis.</summary>
    Standard = 0,

    /// <summary>More saturated branches, with the age bands along plasma.</summary>
    Vivid = 1,

    /// <summary>Light, pale branches, with the age bands along viridis washed towards white.</summary>
    Soft = 2,

    /// <summary>Darker branches, with the age bands along magma.</summary>
    Deep = 3,
}
