using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// The map changing from one view to another: each of the largest shapes a flat shape in its own
/// colour, its outline blended on the compositor from where the view left draws it to where the view
/// arrived at does, over the two drawings crossfading underneath (<see cref="ViewMorph"/>).
///
/// <para>Decoration only. The shapes go when the move lands, leaving the new view's drawing, which
/// has faded in under them over the same time and is the record of what is where.</para>
///
/// <para>Which shapes travel, their outlines, their colours and how visible they are over the move
/// (<see cref="MorphEnvelope"/>) are Core's; this only builds them and sets them moving, on a stage of
/// their own (<see cref="ExploreMoveStage"/>) (G1).</para>
/// </summary>
internal sealed class ExploreMorph(Compositor compositor, MapGeometry geometry)
{
    private readonly Compositor _compositor = compositor;

    private readonly MapGeometry _geometry = geometry;

    /// <summary>
    /// Put <paramref name="morphs"/> on <paramref name="stage"/>, on a canvas of
    /// <paramref name="width"/> by <paramref name="height"/>, and move them over
    /// <paramref name="motion"/>, which travels.
    /// </summary>
    public void Build(VisualCollection stage, IReadOnlyList<ShapeMorph> morphs, int width, int height, Motion motion)
    {
        var shapes = _compositor.CreateShapeVisual();
        shapes.Size = new Vector2(width, height);

        foreach (var morph in morphs)
        {
            shapes.Shapes.Add(Shape(morph, motion));
        }

        shapes.Opacity = 0;
        shapes.StartAnimation(nameof(Visual.Opacity), Envelope(motion));
        stage.InsertAtTop(shapes);
    }

    /// <summary>Let go of the shapes <see cref="Build"/> made, and what each of them draws with.</summary>
    public static void Release(Visual visual)
    {
        var shapes = (ShapeVisual)visual;

        foreach (var shape in shapes.Shapes.OfType<CompositionSpriteShape>())
        {
            shape.FillBrush.Dispose();

            // Only in a high contrast theme's colours: see Shape.
            shape.StrokeBrush?.Dispose();
            shape.Geometry.Dispose();
            shape.Dispose();
        }

        shapes.Dispose();
    }

    private CompositionSpriteShape Shape(ShapeMorph morph, Motion motion)
    {
        var from = _geometry.Figure(morph.From);
        var to = _geometry.Figure(morph.To);
        var geometry = _compositor.CreatePathGeometry(from);
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.FillBrush = _compositor.CreateColorBrush(morph.Colour.ToColor());

        // In a high contrast theme's colours the fill is the ground's, and the edge is what travels.
        if (morph.Edges is { } edges)
        {
            shape.StrokeBrush = _compositor.CreateColorBrush(edges.Colour.ToColor());
            shape.StrokeThickness = edges.Width;
        }

        var travel = _compositor.CreatePathKeyFrameAnimation();
        travel.InsertKeyFrame(0, from);
        travel.InsertKeyFrame(1, to, _compositor.MotionEase());
        travel.Duration = motion.Duration;
        geometry.StartAnimation(nameof(CompositionPathGeometry.Path), travel);

        return shape;
    }

    private ScalarKeyFrameAnimation Envelope(Motion motion)
    {
        var envelope = _compositor.CreateScalarKeyFrameAnimation();

        foreach (var (progress, opacity) in MorphEnvelope.Steps)
        {
            envelope.InsertKeyFrame(progress, opacity);
        }

        envelope.Duration = motion.Duration;

        return envelope;
    }
}
