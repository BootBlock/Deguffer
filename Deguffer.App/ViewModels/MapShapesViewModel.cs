using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The appearance window's Shapes section: how the inside of every shape is lit, the bevel along its
/// edges, and the light under the pointer. Each box is indexed against the enum it sets, in the enum's
/// order.
/// </summary>
public sealed class MapShapesViewModel : MapAppearanceSection
{
    internal MapShapesViewModel(MapAppearanceViewModel window)
        : base(
            window,
            [
                nameof(ShadingIndex),
                nameof(ReliefIndex),
                nameof(BevelShadesIndex),
                nameof(BevelWidthIndex),
                nameof(Raised),
                nameof(Bevelled),
                nameof(PointerLightShown),
                nameof(LightStrengthIndex),
                nameof(LightReachIndex),
            ])
    {
    }

    /// <summary>Index into the shading list, ordered to match <see cref="MapShading"/>.</summary>
    public int ShadingIndex
    {
        get => (int)Look.Style.Shading;
        set => Window.Restyle(value, style => style with { Shading = (MapShading)value });
    }

    /// <summary>Index into the relief list, ordered to match <see cref="MapRelief"/>.</summary>
    public int ReliefIndex
    {
        get => (int)Look.Style.Relief;
        set => Window.Restyle(value, style => style with { Relief = (MapRelief)value });
    }

    /// <summary>Index into the list of a bevel's shades, ordered to match <see cref="MapBevelShades"/>.</summary>
    public int BevelShadesIndex
    {
        get => (int)Look.Style.BevelShades;
        set => Window.Restyle(value, style => style with { BevelShades = (MapBevelShades)value });
    }

    /// <summary>Index into the list of a bevel's widths, ordered to match <see cref="MapBevelWidth"/>.</summary>
    public int BevelWidthIndex
    {
        get => (int)Look.Style.BevelWidth;
        set => Window.Restyle(value, style => style with { BevelWidth = (MapBevelWidth)value });
    }

    /// <summary>Whether the depth box changes anything under the shading chosen (<see cref="MapStyle.Raised"/>).</summary>
    public bool Raised => Look.Style.Raised;

    /// <summary>Whether the bevel's boxes change anything under the shading chosen (<see cref="MapStyle.Bevelled"/>).</summary>
    public bool Bevelled => Look.Style.Bevelled;

    /// <summary>Whether the map shines a soft light over the point under the pointer.</summary>
    public bool PointerLightShown
    {
        get => Look.Light.Shown;
        set => Window.Relight(light => light with { Shown = value });
    }

    /// <summary>Index into the light's strength list, ordered to match <see cref="MapLightStrength"/>.</summary>
    public int LightStrengthIndex
    {
        get => (int)Look.Light.Strength;
        set => Window.Relight(value, light => light with { Strength = (MapLightStrength)value });
    }

    /// <summary>Index into the light's reach list, ordered to match <see cref="MapLightReach"/>.</summary>
    public int LightReachIndex
    {
        get => (int)Look.Light.Reach;
        set => Window.Relight(value, light => light with { Reach = (MapLightReach)value });
    }
}
