using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The shape under the pointer, lifted out of the picture: the drawing's own pixels in that shape,
/// with a soft shadow round them, over whatever is drawn over the rest of the picture.
///
/// <para>A copy of the drawing masked to the shape rather than anything drawn of its own, so what is
/// lifted is exactly what was there, and its neighbours stay as they are. A mask rather than a clip,
/// because a shadow is cast by a visual's content before any clip, and a clipped canvas cast the
/// whole canvas's shadow over the map. The same mask shapes the shadow.</para>
///
/// <para>One sprite over the part of the canvas the shape covers, with a shadow of its own and a mask
/// this fills each time the shape changes. Not a layer visual holding a canvas-sized sprite masked
/// by a visual surface: at the end of a zoom the compositor composed that layer again only where it
/// had been before, so the shape stayed lifted by its outline from before the zoom, or not at all
/// where it had grown, until the pointer moved to another shape.</para>
///
/// <para>Separate from <see cref="ExploreHighlight"/>, which decides which shape is lifted and when
/// effects are drawn at all; this is how one shape is lifted (G1).</para>
/// </summary>
internal sealed class ExploreLift
{
    /// <summary>
    /// The shadow, in device-independent pixels on screen: soft, and a little below, as from a light
    /// above the screen.
    /// </summary>
    private const float ShadowBlur = 10;

    private const float ShadowDrop = 2;

    private readonly MapGraphics _graphics;

    /// <summary>Where the canvas lies in the picture, which the camera above it places on the screen.</summary>
    private readonly ContainerVisual _placed;

    /// <summary>The part of the canvas the shape covers (<see cref="MapLift.Area"/>), placed at that part.</summary>
    private readonly SpriteVisual _sprite;

    /// <summary>The drawing's surface, one pixel of it to one unit of the sprite, as its own layer has it.</summary>
    private readonly CompositionSurfaceBrush _drawing;

    /// <summary>The mask's surface, the size of the part of the canvas the shape covers.</summary>
    private readonly CompositionSurfaceBrush _mask;

    /// <summary>The surface the shape is filled into, resized for each shape rather than made again (G5).</summary>
    private CompositionDrawingSurface? _maskSurface;

    public ExploreLift(Compositor compositor, MapGraphics graphics, MapCamera camera)
    {
        _graphics = graphics;

        _drawing = compositor.CreateUnstretchedBrush();
        _mask = compositor.CreateUnstretchedBrush();

        var masked = compositor.CreateMaskBrush();
        masked.Source = _drawing;
        masked.Mask = _mask;

        _sprite = compositor.CreateSpriteVisual();
        _sprite.Brush = masked;

        var shadow = compositor.CreateDropShadow();
        shadow.Color = Color.FromArgb(153, 0, 0, 0);
        shadow.Mask = _mask;
        _sprite.Shadow = shadow;

        _placed = compositor.CreateContainerVisual();
        _placed.Children.InsertAtTop(_sprite);
        _placed.IsVisible = false;

        // The canvas is magnified by the camera and by its placement, so the shadow is divided by
        // both to stay the same size on screen, as the outlines' strokes are.
        shadow.StartAnimation(nameof(DropShadow.BlurRadius), camera.Unmagnified(ShadowBlur, _placed));
        shadow.StartAnimation(nameof(DropShadow.Offset), camera.UnmagnifiedDown(ShadowDrop, _placed));
    }

    /// <summary>The top of the lifted shape in the composition tree, under the camera.</summary>
    public Visual Root => _placed;

    /// <summary>
    /// Lift <paramref name="shape"/>, the geometry of <paramref name="outlines"/>, in the pixels of a
    /// canvas <paramref name="canvas"/> across that is painted on <paramref name="surface"/>; or lift
    /// nothing, where either is missing or the shape covers none of the canvas.
    /// </summary>
    public void Show(CanvasGeometry? shape, IReadOnlyList<ExploreOutline> outlines, ICompositionSurface? surface, Vector2 canvas)
    {
        var area = shape is not null && surface is not null
            ? MapLift.Area(outlines, (int)canvas.X, (int)canvas.Y)
            : null;

        if (area is not { } covered || _graphics.Fill(_maskSurface, shape!, covered) is not { } mask)
        {
            _drawing.Surface = null;
            _placed.IsVisible = false;
            return;
        }

        _maskSurface = mask;
        _mask.Surface = mask;
        _drawing.Surface = surface;
        _drawing.Offset = new Vector2(-covered.X, -covered.Y);

        _sprite.Offset = new Vector3(covered.X, covered.Y, 0);
        _sprite.Size = new Vector2(covered.Width, covered.Height);
        _placed.IsVisible = true;
    }

    /// <summary>Lay it over a canvas that lies at <paramref name="placed"/> in the picture.</summary>
    public void PlaceOver(MapTransform placed) => _placed.Place(placed);
}
