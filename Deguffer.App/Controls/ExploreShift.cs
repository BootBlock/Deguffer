using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// The map moving from one layout of the same place to the next: each large shape a sprite showing
/// its own rectangle of a drawing, travelling on the compositor from where it was to where it is, over
/// the rest of the picture crossfading underneath (<see cref="ShapeChanges"/>, <see cref="ShapeTravel"/>).
///
/// <para>Nothing is drawn during the move. A sprite shows a drawing already painted, the later one
/// shaded at the shape's final size or the earlier one for a shape that went, and the compositor
/// scales it, so the move costs the same whatever the tree holds. The sprites go when the move lands,
/// leaving the later drawing, which has faded in under them over the same time.</para>
///
/// <para>Where the sprites are, and which drawing each shows, is Core's; this only builds them and
/// runs them (G1).</para>
/// </summary>
internal sealed class ExploreShift
{
    private readonly Compositor _compositor;

    /// <summary>Follows the map's camera, as every drawing does.</summary>
    private readonly ContainerVisual _camera;

    /// <summary>Where the canvas both drawings share lies in the picture, which the camera places on the screen.</summary>
    private readonly ContainerVisual _placed;

    /// <summary>The move under way, until its animations have finished; null while there is none.</summary>
    private CompositionScopedBatch? _moving;

    private CompositionEasingFunction? _ease;

    public ExploreShift(Compositor compositor, MapCamera camera)
    {
        _compositor = compositor;
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
    /// Move <paramref name="travels"/> as <paramref name="motion"/> says, each showing
    /// <paramref name="earlier"/> or <paramref name="later"/>, two drawings on a canvas that lies at
    /// <paramref name="placed"/> in the picture. Nothing moves for a motion that does not travel: the
    /// new layout fades in place.
    /// </summary>
    public void Start(
        IReadOnlyList<ShapeTravel> travels,
        ICompositionSurface earlier,
        ICompositionSurface later,
        MapTransform placed,
        Motion motion)
    {
        Finish();

        if (travels.Count == 0 || !motion.Travels || motion.IsInstant)
        {
            return;
        }

        _placed.Scale = new Vector3((float)placed.ScaleX, (float)placed.ScaleY, 1);
        _placed.Offset = new Vector3((float)placed.X, (float)placed.Y, 0);

        _ease ??= _compositor.CreateCubicBezierEasingFunction(Motion.EaseControlPoints.First, Motion.EaseControlPoints.Second);

        var moving = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        foreach (var travel in travels)
        {
            _placed.Children.InsertAtTop(Sprite(travel, travel.ShowsEarlier ? earlier : later, motion));
        }

        moving.End();
        moving.Completed += (sender, _) =>
        {
            // A move finished early has gone already, and a later one may be under way.
            if (ReferenceEquals(sender, _moving))
            {
                Finish();
            }
        };

        _moving = moving;
    }

    /// <summary>End the move where it was going: the sprites go, leaving the later drawing.</summary>
    public void Finish()
    {
        if (_moving is null)
        {
            return;
        }

        _moving = null;

        foreach (var sprite in _placed.Children.OfType<SpriteVisual>().ToList())
        {
            _placed.Children.Remove(sprite);
            sprite.Brush?.Dispose();
            sprite.Dispose();
        }

        Arrived?.Invoke(this, EventArgs.Empty);
    }

    private SpriteVisual Sprite(ShapeTravel travel, ICompositionSurface surface, Motion motion)
    {
        // One surface pixel to one unit of the sprite, read from the shape's corner, so the sprite is
        // the shape's own rectangle of the drawing and nothing round it.
        var brush = _compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.None;
        brush.HorizontalAlignmentRatio = 0;
        brush.VerticalAlignmentRatio = 0;
        brush.Offset = new Vector2(-(float)travel.Source.X, -(float)travel.Source.Y);

        var sprite = _compositor.CreateSpriteVisual();
        sprite.Brush = brush;
        sprite.Size = new Vector2((float)travel.Source.Width, (float)travel.Source.Height);
        sprite.Offset = Offset(travel.Start);
        sprite.Scale = Scale(travel.Start);

        sprite.StartAnimation(nameof(Visual.Offset), Towards(Offset(travel.Start), Offset(travel.End), motion));
        sprite.StartAnimation(nameof(Visual.Scale), Towards(Scale(travel.Start), Scale(travel.End), motion));

        return sprite;
    }

    private Vector3KeyFrameAnimation Towards(Vector3 from, Vector3 to, Motion motion)
    {
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, from);
        animation.InsertKeyFrame(1, to, _ease);
        animation.Duration = motion.Duration;

        return animation;
    }

    private static Vector3 Offset(SpritePlacement placement) => new((float)placement.X, (float)placement.Y, 0);

    private static Vector3 Scale(SpritePlacement placement) => new((float)placement.ScaleX, (float)placement.ScaleY, 1);
}
