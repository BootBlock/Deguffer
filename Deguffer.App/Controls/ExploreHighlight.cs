using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// What is drawn over the map to say the state of its shapes: the outlines round what is picked and
/// what the pointer is over, the shape under the pointer lifted with a shadow, the rest of the
/// picture dimmed round a selection, and a hatch over what a removal under way is acting on.
///
/// <para>Over the picture rather than in it, which is what the reference implementation does and for
/// the reason it does it: WinDirStat renders the shapes into a cached surface once and draws only the
/// selection live over the top. Baked into the picture instead, every click would rasterise the whole
/// volume again to move one outline.</para>
///
/// <para>In the same composition tree as the drawings, placed in the picture the way the drawing it
/// marks out is and moved by the same camera, so all of it goes wherever its shape goes on screen,
/// through a zoom, a drag or a resize, by construction. Every one of them is drawn from the outlines
/// the drawing gives for the nodes it is told, which are the shapes a click on it resolves to, so
/// nothing is marked that is not what it says (§7.1).</para>
///
/// <para>The shadow and the dimming are effects, for a reader who can see them: off in high contrast,
/// where the outlines and the hatch take system colours instead (§6.5), and off where the compositor
/// says effects are slow, over Remote Desktop for one. The hatch is plain geometry and says what is
/// about to go, so it stays.</para>
///
/// <para>One type for all four, past the usual length, because they are one answer: each is drawn
/// from the outlines of the same drawing, placed over it by the same placement, and turned to the
/// reader's colours and effects by the same switch, and a mark drawn from a different one would mark a
/// different shape. How a shape is lifted is apart, in <see cref="ExploreLift"/>.</para>
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

    /// <summary>
    /// How far past the picture's edges an outline is still drawn, in the picture's own
    /// device-independent pixels: as far as the widest stroke reaches past the shape it runs round.
    /// The camera only ever magnifies, because the zoom is never under 1, so no stroke reaches
    /// further in the picture's units than it does on screen.
    /// </summary>
    private const float Margin = HoveredHaloWidth;

    /// <summary>
    /// How dark the picture goes round a selection: enough that the picked shapes stand out on a dense
    /// map at a glance, not so much that what is round them can no longer be read.
    /// </summary>
    private const float Dimming = 0.3f;

    /// <summary>
    /// How far apart the hatch's stripes are, and how wide each is, on the screen at rest. They are
    /// laid out again with each new drawing, and grow and shrink with the picture until it arrives.
    /// </summary>
    private const float HatchSpacing = 8;

    private const float HatchWidth = HatchSpacing / 2;

    private readonly MapGraphics _graphics;

    /// <summary>Where the picture is on the screen: the map's camera, followed.</summary>
    private readonly ContainerVisual _camera;

    /// <summary>
    /// The area the outlines are drawn in: the picture, and <see cref="Margin"/> round it. A shape
    /// visual draws nothing past its own size, and the halo round a shape at the picture's edge lies
    /// half outside it, where it has always been drawn whole.
    /// </summary>
    private readonly ShapeVisual _bounds;

    /// <summary>The outlines and the hatch, in the canvas's own pixels, placed where that canvas lies in the picture.</summary>
    private readonly CompositionContainerShape _placed;

    /// <summary>The dimming, under the lifted shape: the picture, cut to its edges, and its placement in it.</summary>
    private readonly ShapeVisual _dimBounds;

    private readonly CompositionContainerShape _dimPlaced;

    private readonly CompositionPathGeometry _dimmed;

    /// <summary>The shape under the pointer, lifted out of the picture over the dimming.</summary>
    private readonly ExploreLift _lift;

    private readonly CompositionPathGeometry _hovered;

    private readonly CompositionPathGeometry _picked;

    private readonly CompositionPathGeometry _marked;

    private readonly CompositionPathGeometry _markedBetween;

    private readonly CompositionColorBrush _hoveredHalo;

    private readonly CompositionColorBrush _hoveredEdge;

    private readonly CompositionColorBrush _pickedHalo;

    private readonly CompositionColorBrush _pickedEdge;

    private readonly CompositionColorBrush _hatch;

    private readonly CompositionColorBrush _hatchBetween;

    private IReadOnlyList<ExploreOutline> _pickedOutlines = [];

    private IReadOnlyList<ExploreOutline> _hoveredOutlines = [];

    private IReadOnlyList<ExploreOutline> _markedOutlines = [];

    /// <summary>
    /// The canvas the shape under the pointer is drawn on, and its size, kept to lift it from when
    /// the effects come back.
    /// </summary>
    private ICompositionSurface? _hoveredSurface;

    private Vector2 _hoveredCanvas;

    /// <summary>The canvas pixels to each device-independent pixel the hatch was last laid out at.</summary>
    private double _hatchScale = 1;

    private bool _effects = true;

    public ExploreHighlight(Compositor compositor, MapGraphics graphics, MapCamera camera)
    {
        _graphics = graphics;

        _camera = compositor.CreateContainerVisual();
        camera.Follow(_camera);

        // The dimming first, so the lifted shape and every line are over it.
        _dimBounds = compositor.CreateShapeVisual();
        _dimPlaced = compositor.CreateContainerShape();
        _dimBounds.Shapes.Add(_dimPlaced);
        _dimmed = compositor.CreatePathGeometry(graphics.Nothing);
        var dim = compositor.CreateSpriteShape(_dimmed);
        dim.FillBrush = compositor.CreateColorBrush(Shade(0, Dimming));
        _dimPlaced.Shapes.Add(dim);
        _camera.Children.InsertAtTop(_dimBounds);

        _lift = new ExploreLift(compositor, graphics, camera);
        _camera.Children.InsertAtTop(_lift.Root);

        _bounds = compositor.CreateShapeVisual();
        _camera.Children.InsertAtTop(_bounds);

        _placed = compositor.CreateContainerShape();
        _bounds.Shapes.Add(_placed);

        _hovered = compositor.CreatePathGeometry(graphics.Nothing);
        _picked = compositor.CreatePathGeometry(graphics.Nothing);
        _marked = compositor.CreatePathGeometry(graphics.Nothing);
        _markedBetween = compositor.CreatePathGeometry(graphics.Nothing);

        _hoveredHalo = compositor.CreateColorBrush();
        _hoveredEdge = compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
        _pickedHalo = compositor.CreateColorBrush();
        _pickedEdge = compositor.CreateColorBrush();
        _hatch = compositor.CreateColorBrush();
        _hatchBetween = compositor.CreateColorBrush();
        Colour(null, _hoveredEdge.Color);

        // Drawn in this order: the hatch under every line, so a shape marked for removal is still
        // outlined, and what is picked over what the pointer is over where they meet.
        _placed.Shapes.Add(Fill(compositor, _markedBetween, _hatchBetween));
        _placed.Shapes.Add(Fill(compositor, _marked, _hatch));
        _placed.Shapes.Add(Stroke(compositor, camera, _hovered, _hoveredHalo, HoveredHaloWidth));
        _placed.Shapes.Add(Stroke(compositor, camera, _hovered, _hoveredEdge, HoveredEdgeWidth));
        _placed.Shapes.Add(Stroke(compositor, camera, _picked, _pickedHalo, PickedHaloWidth));
        _placed.Shapes.Add(Stroke(compositor, camera, _picked, _pickedEdge, PickedEdgeWidth));
    }

    /// <summary>The top of what is drawn over the picture, in the composition tree.</summary>
    public Visual Root => _camera;

    /// <summary>Mark out what the user picked, and dim the rest of the picture round it.</summary>
    public void ShowPicked(IReadOnlyList<ExploreOutline> outlines)
    {
        _pickedOutlines = outlines;
        _picked.Path = _graphics.Trace(outlines);
        _dimmed.Path = _effects ? _graphics.Around(outlines) : _graphics.Nothing;
    }

    /// <summary>
    /// Mark out what the pointer is over, and lift it out of <paramref name="surface"/>, the canvas
    /// it is drawn on, which is <paramref name="canvas"/> pixels across. Null where that canvas is not
    /// on screen whole, and the outline alone marks it.
    /// </summary>
    public void ShowHovered(IReadOnlyList<ExploreOutline> outlines, ICompositionSurface? surface, Vector2 canvas)
    {
        _hoveredOutlines = outlines;
        _hoveredSurface = surface;
        _hoveredCanvas = canvas;
        _hovered.Path = _graphics.Trace(outlines);

        _lift.Show(_hovered.Path, _effects ? surface : null, canvas);
    }

    /// <summary>
    /// Hatch what a removal under way is acting on, at <paramref name="scale"/> canvas pixels to each
    /// device-independent pixel, until it is told the removal is over with nothing to hatch.
    /// </summary>
    public void ShowMarked(IReadOnlyList<ExploreOutline> outlines, double scale)
    {
        _markedOutlines = outlines;
        _hatchScale = scale;

        var spacing = (float)(HatchSpacing * scale);
        var width = (float)(HatchWidth * scale);

        _marked.Path = _graphics.Hatch(outlines, spacing, width, 0);
        _markedBetween.Path = _graphics.Hatch(outlines, spacing, width, width);
    }

    /// <summary>
    /// Take on the reader's colours: <paramref name="accent"/> for what the pointer is over, or the
    /// high contrast theme's own colours where <paramref name="system"/> gives them, with the effects
    /// off. <paramref name="effectsFast"/> says whether the compositor can draw effects without
    /// slowing the picture down. Told rather than read here, because the map already follows the
    /// system's settings and a second listener would be a second copy of the same window onto them
    /// (G5).
    /// </summary>
    public void Restyle(Color accent, SystemHighlight? system, bool effectsFast)
    {
        Colour(system, accent);

        var effects = system is null && effectsFast;

        if (effects == _effects)
        {
            return;
        }

        _effects = effects;
        _dimBounds.IsVisible = effects;

        ShowPicked(_pickedOutlines);
        ShowHovered(_hoveredOutlines, _hoveredSurface, _hoveredCanvas);
        ShowMarked(_markedOutlines, _hatchScale);
    }

    /// <summary>
    /// Lay all of it over a canvas that lies at <paramref name="placed"/> in a picture
    /// <paramref name="width"/> by <paramref name="height"/> device-independent pixels across, measured
    /// from <paramref name="origin"/>.
    ///
    /// <para>A shape visual draws nothing past its own bounds, so they are the whole picture and a
    /// margin round it, wherever the origin puts the picture: from the origin, the picture's corner is
    /// behind and above it.</para>
    /// </summary>
    public void PlaceOver(MapTransform placed, double width, double height, MapOrigin origin)
    {
        var corner = new Vector3((float)(-origin.Left * width) - Margin, (float)(-origin.Top * height) - Margin, 0);
        var size = new Vector2((float)width + (2 * Margin), (float)height + (2 * Margin));
        var scale = new Vector2((float)placed.ScaleX, (float)placed.ScaleY);
        var offset = new Vector2((float)(placed.X - corner.X), (float)(placed.Y - corner.Y));

        _bounds.Offset = corner;
        _bounds.Size = size;
        _placed.Scale = scale;
        _placed.Offset = offset;

        _dimBounds.Offset = corner;
        _dimBounds.Size = size;
        _dimPlaced.Scale = scale;
        _dimPlaced.Offset = offset;

        _lift.PlaceOver(placed);
    }

    /// <summary>
    /// Take all of it off the screen while the picture under it is on its way to being another,
    /// keeping it to put back.
    /// </summary>
    public void Hide() => _camera.IsVisible = false;

    /// <summary>Put it back once the picture it marks out is the one on screen.</summary>
    public void Reveal() => _camera.IsVisible = true;

    /// <summary>Take all of it off, for a map that is no longer showing anything.</summary>
    public void Clear()
    {
        ShowPicked([]);
        ShowHovered([], null, _hoveredCanvas);
        ShowMarked([], _hatchScale);
    }

    private static Color Shade(byte level, double opacity) =>
        Color.FromArgb((byte)Math.Round(opacity * 255), level, level, level);

    /// <summary>
    /// Every colour drawn here: the high contrast theme's where <paramref name="system"/> gives them,
    /// and the shades that read over every hue of the picture otherwise.
    /// </summary>
    private void Colour(SystemHighlight? system, Color accent)
    {
        if (system is { } colours)
        {
            _hoveredHalo.Color = colours.Ground;
            _hoveredEdge.Color = colours.Hovered;
            _pickedHalo.Color = colours.Ground;
            _pickedEdge.Color = colours.Picked;
            _hatch.Color = colours.Picked;
            _hatchBetween.Color = Color.FromArgb(0, 0, 0, 0);
            return;
        }

        _hoveredHalo.Color = Shade(0, 0.75);
        _hoveredEdge.Color = accent;
        _pickedHalo.Color = Shade(0, 0.6);
        _pickedEdge.Color = Shade(255, 1);

        // Dark and light by turns, so the hatch reads as one over a pale shape and a deep one alike.
        _hatch.Color = Shade(0, 0.45);
        _hatchBetween.Color = Shade(255, 0.3);
    }

    private static CompositionSpriteShape Fill(Compositor compositor, CompositionGeometry geometry, CompositionBrush brush)
    {
        var shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = brush;

        return shape;
    }

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

/// <summary>
/// The high contrast theme's colours for what is drawn over the map: the ground the lines are drawn
/// against, and the colours it gives what is selected and what the pointer is over.
/// </summary>
internal readonly record struct SystemHighlight(Color Ground, Color Picked, Color Hovered);
