using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The shape under the pointer, lifted out of the picture: the drawing's own pixels in that shape,
/// with a soft shadow round them, over whatever is drawn over the rest of the picture.
///
/// <para>A copy of the drawing masked to the shape rather than anything drawn of its own, so what is
/// lifted is exactly what was there, and its neighbours stay as they are. A mask rather than a clip,
/// because a layer's shadow is cast by its content before any clip, and a clipped canvas cast the
/// whole canvas's shadow over the map.</para>
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

    private readonly LayerVisual _layer;

    private readonly SpriteVisual _sprite;

    /// <summary>The drawing's surface, one pixel of it to one unit of the sprite, as its own layer has it.</summary>
    private readonly CompositionSurfaceBrush _drawing;

    /// <summary>The shape, filled, drawn into the surface the mask is read from.</summary>
    private readonly ShapeVisual _maskShape;

    private readonly CompositionVisualSurface _maskSurface;

    private readonly CompositionPathGeometry _shape;

    public ExploreLift(Compositor compositor, MapGraphics graphics, MapCamera camera)
    {
        _graphics = graphics;

        _drawing = Unstretched(compositor.CreateSurfaceBrush());

        _shape = compositor.CreatePathGeometry(graphics.Nothing);
        var fill = compositor.CreateSpriteShape(_shape);
        fill.FillBrush = compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
        _maskShape = compositor.CreateShapeVisual();
        _maskShape.Shapes.Add(fill);
        _maskSurface = compositor.CreateVisualSurface();
        _maskSurface.SourceVisual = _maskShape;

        var masked = compositor.CreateMaskBrush();
        masked.Source = _drawing;
        masked.Mask = Unstretched(compositor.CreateSurfaceBrush(_maskSurface));

        _sprite = compositor.CreateSpriteVisual();
        _sprite.Brush = masked;

        var shadow = compositor.CreateDropShadow();
        shadow.Color = Color.FromArgb(153, 0, 0, 0);
        shadow.SourcePolicy = CompositionDropShadowSourcePolicy.InheritFromVisualContent;

        _layer = compositor.CreateLayerVisual();
        _layer.Shadow = shadow;
        _layer.Children.InsertAtTop(_sprite);

        _placed = compositor.CreateContainerVisual();
        _placed.Children.InsertAtTop(_layer);
        _placed.IsVisible = false;

        // The canvas is magnified by the camera and by its placement, so the shadow is divided by
        // both to stay the same size on screen, as the outlines' strokes are.
        Unmagnified(compositor, camera, shadow, nameof(DropShadow.BlurRadius),
            $"{ShadowBlur} / Max(0.0001, Max(camera.Scale.X * placed.Scale.X, camera.Scale.Y * placed.Scale.Y))");
        Unmagnified(compositor, camera, shadow, nameof(DropShadow.Offset),
            $"Vector3(0, {ShadowDrop} / Max(0.0001, camera.Scale.Y * placed.Scale.Y), 0)");
    }

    /// <summary>The top of the lifted shape in the composition tree, under the camera.</summary>
    public Visual Root => _placed;

    /// <summary>
    /// Lift <paramref name="shape"/>, in the pixels of a canvas <paramref name="canvas"/> across that is
    /// painted on <paramref name="surface"/>; or lift nothing, where either is missing.
    /// </summary>
    public void Show(CompositionPath shape, ICompositionSurface? surface, Vector2 canvas)
    {
        var lifted = surface is not null && shape != _graphics.Nothing;

        _drawing.Surface = lifted ? surface : null;
        _shape.Path = lifted ? shape : _graphics.Nothing;
        _layer.Size = canvas;
        _sprite.Size = canvas;
        _maskShape.Size = canvas;
        _maskSurface.SourceSize = canvas;
        _placed.IsVisible = lifted;
    }

    /// <summary>Lay it over a canvas that lies at <paramref name="placed"/> in the picture.</summary>
    public void PlaceOver(MapTransform placed)
    {
        _placed.Scale = new Vector3((float)placed.ScaleX, (float)placed.ScaleY, 1);
        _placed.Offset = new Vector3((float)placed.X, (float)placed.Y, 0);
    }

    /// <summary>One surface pixel to one unit of the visual, from its top-left corner.</summary>
    private static CompositionSurfaceBrush Unstretched(CompositionSurfaceBrush brush)
    {
        brush.Stretch = CompositionStretch.None;
        brush.HorizontalAlignmentRatio = 0;
        brush.VerticalAlignmentRatio = 0;

        return brush;
    }

    private void Unmagnified(Compositor compositor, MapCamera camera, CompositionObject target, string property, string expression)
    {
        var animation = compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("camera", camera.Properties);
        animation.SetReferenceParameter("placed", _placed);

        target.StartAnimation(property, animation);
    }
}
