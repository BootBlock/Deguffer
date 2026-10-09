using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The outlines drawn over the map: what is picked, and what the pointer is over.
///
/// <para>Over the picture rather than in it, which is what the reference implementation does and for
/// the reason it does it: WinDirStat renders the shapes into a cached surface once and draws only the
/// selection live over the top. Baked into the picture instead, every click would rasterise the whole
/// volume again to move one outline.</para>
///
/// <para>In the same composition tree as the drawings, placed in the picture the way the drawing it
/// outlines is and moved by the same camera, so an outline goes wherever its shape goes on screen,
/// through a zoom, a drag or a resize, by construction.</para>
///
/// <para>Separate from <see cref="ExploreMap"/> because the two answer different questions. That one
/// is about which tree is drawn and what the pointer found; this one is about marking a shape out
/// once somebody else has decided which shape it is (G1).</para>
/// </summary>
internal sealed class ExploreHighlight
{
    /// <summary>
    /// Two strokes, dark under light, rather than one in a colour chosen to contrast.
    ///
    /// <para>§6.5 asks for the UI to read on a flat background in either theme, and a treemap is a
    /// harder case than that: the shape underneath is any of eight hues at any of four lightnesses,
    /// shaded from bright at its middle to dark at its edges, and it is the <em>edge</em> an outline
    /// runs along. No single colour survives that. A dark line with a light one inside it is legible
    /// against every one of them, is the same in both themes, and is what a selection marquee has
    /// looked like for long enough that nobody has to be told what it means.</para>
    /// </summary>
    private const float PickedHaloWidth = 3.5f;

    private const float PickedEdgeWidth = 1.75f;

    /// <summary>
    /// The system accent colour over a dark halo, where the picked outline is white. What the
    /// pointer is over is about to be picked; what is picked is what Delete acts on, and the two
    /// must not read as the same claim, so they differ by colour, which a glance takes in, rather
    /// than by strength.
    ///
    /// <para>The halo is nearly opaque because it is what carries a line across every hue: a faint
    /// one all but vanishes along the dark edge of a shaded tile. The accent is the colour Windows
    /// marks hover and focus with, so it needs no explaining.</para>
    /// </summary>
    private const float HoveredHaloWidth = 4.5f;

    private const float HoveredEdgeWidth = 2;

    private readonly MapGraphics _graphics;

    /// <summary>Where the picture is on the screen: the map's camera, followed.</summary>
    private readonly ContainerVisual _camera;

    /// <summary>The outlines, in the canvas's own pixels, placed where that canvas lies in the picture.</summary>
    private readonly ShapeVisual _placed;

    private readonly CompositionPathGeometry _hovered;

    private readonly CompositionPathGeometry _picked;

    private readonly CompositionColorBrush _accent;

    public ExploreHighlight(Compositor compositor, MapGraphics graphics, MapCamera camera)
    {
        _graphics = graphics;

        _camera = compositor.CreateContainerVisual();
        _placed = compositor.CreateShapeVisual();
        _camera.Children.InsertAtTop(_placed);
        camera.Follow(_camera);

        _hovered = compositor.CreatePathGeometry(graphics.Nothing);
        _picked = compositor.CreatePathGeometry(graphics.Nothing);
        _accent = compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));

        // Drawn in this order, so what is picked is over what the pointer is over where they meet.
        _placed.Shapes.Add(Stroke(compositor, camera, _hovered, compositor.CreateColorBrush(Shade(0, 0.75)), HoveredHaloWidth));
        _placed.Shapes.Add(Stroke(compositor, camera, _hovered, _accent, HoveredEdgeWidth));
        _placed.Shapes.Add(Stroke(compositor, camera, _picked, compositor.CreateColorBrush(Shade(0, 0.6)), PickedHaloWidth));
        _placed.Shapes.Add(Stroke(compositor, camera, _picked, compositor.CreateColorBrush(Shade(255, 1)), PickedEdgeWidth));
    }

    /// <summary>The top of the outlines in the composition tree.</summary>
    public Visual Root => _camera;

    /// <summary>Mark out what the user picked.</summary>
    public void ShowPicked(IReadOnlyList<ExploreOutline> outlines) => _picked.Path = _graphics.Trace(outlines);

    /// <summary>
    /// Draw what the pointer is over in <paramref name="accent"/>. Told rather than read here,
    /// because the map already follows the system's settings and a second listener would be a
    /// second copy of the same window onto them (G5).
    /// </summary>
    public void TintHovered(Color accent) => _accent.Color = accent;

    /// <summary>Mark out what the pointer is over.</summary>
    public void ShowHovered(IReadOnlyList<ExploreOutline> outlines) => _hovered.Path = _graphics.Trace(outlines);

    /// <summary>
    /// Lay the outlines over a canvas <paramref name="width"/> by <paramref name="height"/> pixels
    /// across that lies at <paramref name="placed"/> in the picture.
    /// </summary>
    public void PlaceOver(int width, int height, MapTransform placed)
    {
        _placed.Size = new Vector2(width, height);
        _placed.Scale = new Vector3((float)placed.ScaleX, (float)placed.ScaleY, 1);
        _placed.Offset = new Vector3((float)placed.X, (float)placed.Y, 0);
    }

    /// <summary>
    /// Take the outlines off the screen while the picture under them is on its way to being another,
    /// keeping them to put back. The labels are hidden on the same terms.
    /// </summary>
    public void Hide() => _camera.IsVisible = false;

    /// <summary>Put the outlines back once the picture they mark out is the one on screen.</summary>
    public void Reveal() => _camera.IsVisible = true;

    /// <summary>Take every outline off, for a map that is no longer showing anything.</summary>
    public void Clear()
    {
        _hovered.Path = _graphics.Nothing;
        _picked.Path = _graphics.Nothing;
    }

    private static Color Shade(byte level, double opacity) =>
        Color.FromArgb((byte)Math.Round(opacity * 255), level, level, level);

    /// <summary>
    /// One stroke round <paramref name="geometry"/>, <paramref name="width"/> device-independent
    /// pixels wide on screen whatever the picture is magnified by.
    ///
    /// <para>The geometry is in the canvas's pixels, which the camera and the canvas's placement
    /// magnify, so the stroke is divided by both, by an expression the compositor works out at every
    /// frame of a move. Divided by the larger of the two axes, which makes the width an upper bound:
    /// the two differ only while a resize settles, and an outline briefly a shade thin reads better
    /// than one briefly heavy, which is what swamps a shape a few pixels across.</para>
    /// </summary>
    private CompositionSpriteShape Stroke(
        Compositor compositor,
        MapCamera camera,
        CompositionGeometry geometry,
        CompositionBrush brush,
        float width)
    {
        var shape = compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = brush;
        shape.StrokeLineJoin = CompositionStrokeLineJoin.Round;

        var thickness = compositor.CreateExpressionAnimation(
            "Width / Max(0.0001, Max(camera.Scale.X * placed.Scale.X, camera.Scale.Y * placed.Scale.Y))");
        thickness.SetScalarParameter("Width", width);
        thickness.SetReferenceParameter("camera", camera.Properties);
        thickness.SetReferenceParameter("placed", _placed);

        shape.StartAnimation(nameof(CompositionSpriteShape.StrokeThickness), thickness);

        return shape;
    }
}
