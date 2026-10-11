using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The map changing from one view to another: each of the largest shapes a flat shape in its own
/// colour, its outline blended on the compositor from where the view left draws it to where the view
/// arrived at does, over the two drawings crossfading underneath (<see cref="ViewMorph"/>).
///
/// <para>Decoration only. The shapes go when the move lands, leaving the new view's drawing, which
/// has faded in under them over the same time and is the record of what is where.</para>
///
/// <para>Which shapes travel, their outlines and their colours are Core's; this only builds them and
/// runs them (G1).</para>
/// </summary>
internal sealed class ExploreMorph
{
    /// <summary>
    /// How far into the move the shapes are fully over the picture, and how far from its end they
    /// start to give way to it. A shape is flat and the drawings under it are shaded, so the shapes
    /// fade in over the view left and out over the view arrived at rather than standing in for either
    /// at once: a cut to them would flatten the picture as the move starts, and a cut from them would
    /// flash the shading on as it lands.
    /// </summary>
    private const float Over = 0.2f;

    private readonly Compositor _compositor;

    private readonly MapGeometry _geometry;

    /// <summary>Follows the map's camera, as every drawing does.</summary>
    private readonly ContainerVisual _camera;

    /// <summary>Where the canvas both views are drawn on lies in the picture, which the camera places on the screen.</summary>
    private readonly ContainerVisual _placed;

    /// <summary>The shapes moving, until their animations have finished; null while none are.</summary>
    private ShapeVisual? _shapes;

    /// <summary>The move under way, until its animations have finished; null while there is none.</summary>
    private CompositionScopedBatch? _moving;

    private CompositionEasingFunction? _ease;

    public ExploreMorph(Compositor compositor, MapGeometry geometry, MapCamera camera)
    {
        _compositor = compositor;
        _geometry = geometry;
        _camera = compositor.CreateContainerVisual();
        _placed = compositor.CreateContainerVisual();
        _camera.Children.InsertAtTop(_placed);
        camera.Follow(_camera);
    }

    /// <summary>Raised once when a move is over, whether it ran its course or was finished early.</summary>
    public event EventHandler? Arrived;

    /// <summary>The top of the moving shapes in the composition tree.</summary>
    public Visual Root => _camera;

    public bool IsMoving => _moving is not null;

    /// <summary>
    /// Move <paramref name="morphs"/>, on a canvas of <paramref name="width"/> by
    /// <paramref name="height"/> that lies at <paramref name="placed"/> in the picture, over
    /// <paramref name="motion"/>, which travels.
    /// </summary>
    public void Start(IReadOnlyList<ShapeMorph> morphs, int width, int height, MapTransform placed, Motion motion)
    {
        Finish();

        _placed.Scale = new Vector3((float)placed.ScaleX, (float)placed.ScaleY, 1);
        _placed.Offset = new Vector3((float)placed.X, (float)placed.Y, 0);

        _ease ??= _compositor.CreateCubicBezierEasingFunction(Motion.EaseControlPoints.First, Motion.EaseControlPoints.Second);

        var moving = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var shapes = _compositor.CreateShapeVisual();
        shapes.Size = new Vector2(width, height);

        foreach (var morph in morphs)
        {
            shapes.Shapes.Add(Shape(morph, motion));
        }

        shapes.Opacity = 0;
        shapes.StartAnimation(nameof(Visual.Opacity), Envelope(motion));
        _placed.Children.InsertAtTop(shapes);

        moving.End();
        moving.Completed += (sender, _) =>
        {
            // A move finished early has gone already, and a later one may be under way.
            if (ReferenceEquals(sender, _moving))
            {
                Finish();
            }
        };

        _shapes = shapes;
        _moving = moving;
    }

    /// <summary>
    /// End the move where it was going: the shapes go. The crossfade under them is the layers' to
    /// bring to its end (<see cref="ExploreLayers.LandFades"/>).
    /// </summary>
    public void Finish()
    {
        if (_moving is null)
        {
            return;
        }

        _moving = null;

        if (_shapes is { } shapes)
        {
            _shapes = null;
            _placed.Children.Remove(shapes);

            foreach (var shape in shapes.Shapes.OfType<CompositionSpriteShape>())
            {
                shape.FillBrush?.Dispose();
                shape.StrokeBrush?.Dispose();
                shape.Geometry?.Dispose();
                shape.Dispose();
            }

            shapes.Dispose();
        }

        Arrived?.Invoke(this, EventArgs.Empty);
    }

    private CompositionSpriteShape Shape(ShapeMorph morph, Motion motion)
    {
        var from = _geometry.Figure(morph.From);
        var to = _geometry.Figure(morph.To);
        var geometry = _compositor.CreatePathGeometry(from);
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.FillBrush = _compositor.CreateColorBrush(Color.FromArgb(255, morph.Colour.Red, morph.Colour.Green, morph.Colour.Blue));

        // In a high contrast theme's colours the fill is the ground's, and the edge is what travels.
        if (morph.Edges is { } edges)
        {
            shape.StrokeBrush = _compositor.CreateColorBrush(Color.FromArgb(255, edges.Colour.Red, edges.Colour.Green, edges.Colour.Blue));
            shape.StrokeThickness = edges.Width;
        }

        var travel = _compositor.CreatePathKeyFrameAnimation();
        travel.InsertKeyFrame(0, from);
        travel.InsertKeyFrame(1, to, _ease);
        travel.Duration = motion.Duration;
        geometry.StartAnimation(nameof(CompositionPathGeometry.Path), travel);

        return shape;
    }

    private ScalarKeyFrameAnimation Envelope(Motion motion)
    {
        var envelope = _compositor.CreateScalarKeyFrameAnimation();
        envelope.InsertKeyFrame(0, 0);
        envelope.InsertKeyFrame(Over, 1);
        envelope.InsertKeyFrame(1 - Over, 1);
        envelope.InsertKeyFrame(1, 0);
        envelope.Duration = motion.Duration;

        return envelope;
    }
}
