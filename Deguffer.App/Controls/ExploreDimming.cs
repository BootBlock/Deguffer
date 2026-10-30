using System.Numerics;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// The rest of the picture dimmed round what a card beside the map lights, or failing that round what
/// is picked, so those shapes stand out on a dense map at a glance.
///
/// <para>An effect, for a reader who can see it: off in high contrast and where the compositor says
/// effects are slow (see <see cref="ShowEffects"/>), where the outlines alone say what is picked.
/// Apart from <see cref="ExploreHighlight"/>, which places it with the outlines, because which shapes
/// it leaves out, and how far it reaches, are its own.</para>
/// </summary>
internal sealed class ExploreDimming
{
    /// <summary>
    /// How dark the picture goes: enough that the shapes left out stand out on a dense map at a glance,
    /// not so much that what is round them can no longer be read.
    /// </summary>
    private const float Dimming = 0.3f;

    private readonly MapGeometry _geometry;

    /// <summary>The picture, cut to its edges, which the dimming is drawn in.</summary>
    private readonly ShapeVisual _bounds;

    /// <summary>The dimming in the canvas's own pixels, placed where that canvas lies in the picture.</summary>
    private readonly CompositionContainerShape _placed;

    private readonly CompositionPathGeometry _dimmed;

    private IReadOnlyList<ExploreOutline> _picked = [];

    /// <summary>What a card beside the map lights, or null while it lights nothing.</summary>
    private IReadOnlyList<ExploreOutline>? _lit;

    /// <summary>How far the dimming reaches, in canvas pixels.</summary>
    private float _far;

    private bool _effects = true;

    public ExploreDimming(Compositor compositor, MapGeometry geometry)
    {
        _geometry = geometry;

        _bounds = compositor.CreateShapeVisual();
        _placed = compositor.CreateContainerShape();
        _bounds.Shapes.Add(_placed);

        _dimmed = compositor.CreatePathGeometry(geometry.Nothing);
        var dim = compositor.CreateSpriteShape(_dimmed);
        dim.FillBrush = compositor.CreateColorBrush(ExploreHighlight.Shade(0, Dimming));
        _placed.Shapes.Add(dim);
    }

    /// <summary>The dimming, in the composition tree.</summary>
    public Visual Root => _bounds;

    /// <summary>
    /// Dim round what the user picked, to <paramref name="far"/> canvas pixels from the canvas's corner
    /// each way (see <see cref="MapGeometry.Around"/>), while no card lights anything.
    /// </summary>
    public void Pick(IReadOnlyList<ExploreOutline> outlines, float far)
    {
        _picked = outlines;
        _far = far;

        Dim();
    }

    /// <summary>
    /// Dim round what a card beside the map points at, in place of the dimming round a selection, for as
    /// long as the card points. Null to go back to the selection, and an empty list to dim all of the
    /// picture, which says that what was asked for is nowhere drawn one by one.
    /// </summary>
    public void Light(IReadOnlyList<ExploreOutline>? outlines)
    {
        _lit = outlines;

        Dim();
    }

    /// <summary>Dim nothing, for a map that is no longer showing anything.</summary>
    public void Clear()
    {
        _picked = [];
        _lit = null;

        Dim();
    }

    /// <summary>Draw the dimming where <paramref name="effects"/> says the reader can have effects, and none otherwise.</summary>
    public void ShowEffects(bool effects)
    {
        if (effects == _effects)
        {
            return;
        }

        _effects = effects;
        _bounds.IsVisible = effects;

        Dim();
    }

    /// <summary>
    /// Lay it over the picture: bounds at <paramref name="corner"/>, <paramref name="size"/> across, and
    /// the canvas at <paramref name="offset"/> in them, magnified by <paramref name="scale"/>. See
    /// <see cref="ExploreHighlight.PlaceOver"/>, which works these out for the outlines too.
    /// </summary>
    public void PlaceOver(Vector3 corner, Vector2 size, Vector2 scale, Vector2 offset)
    {
        _bounds.Offset = corner;
        _bounds.Size = size;
        _placed.Scale = scale;
        _placed.Offset = offset;
    }

    /// <summary>Dim round what is lit, or failing that round what is picked, where the effects are on.</summary>
    private void Dim() =>
        _dimmed.Path = !_effects
            ? _geometry.Nothing
            : _lit switch
            {
                null => _geometry.Around(_picked, _far),
                [] => _geometry.Everything(_far),
                var lit => _geometry.Around(lit, _far),
            };
}
