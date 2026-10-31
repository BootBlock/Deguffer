using System.Numerics;
using Deguffer.App.Shell;
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
/// sets them moving, on a stage of their own (<see cref="ExploreMoveStage"/>) (G1).</para>
/// </summary>
internal sealed class ExploreShift(Compositor compositor)
{
    private readonly Compositor _compositor = compositor;

    /// <summary>
    /// Put <paramref name="travels"/> on <paramref name="stage"/>, each showing
    /// <paramref name="earlier"/> or <paramref name="later"/>, two drawings of the canvas the stage is
    /// placed at, and move each as it says over <paramref name="motion"/>, which travels.
    /// </summary>
    public void Build(
        VisualCollection stage,
        IReadOnlyList<ShapeTravel> travels,
        ICompositionSurface earlier,
        ICompositionSurface later,
        Motion motion)
    {
        foreach (var travel in travels)
        {
            stage.InsertAtTop(Sprite(travel, travel.ShowsEarlier ? earlier : later, motion));
        }
    }

    /// <summary>Let go of a sprite <see cref="Build"/> made, and its brush.</summary>
    public static void Release(Visual visual)
    {
        var sprite = (SpriteVisual)visual;

        sprite.Brush.Dispose();
        sprite.Dispose();
    }

    private SpriteVisual Sprite(ShapeTravel travel, ICompositionSurface surface, Motion motion)
    {
        // Read from the shape's corner, so the sprite is the shape's own rectangle of the drawing and
        // nothing round it.
        var brush = _compositor.CreateUnstretchedBrush(surface);
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
        animation.InsertKeyFrame(1, to, _compositor.MotionEase());
        animation.Duration = motion.Duration;

        return animation;
    }

    private static Vector3 Offset(SpritePlacement placement) => new((float)placement.X, (float)placement.Y, 0);

    private static Vector3 Scale(SpritePlacement placement) => new((float)placement.ScaleX, (float)placement.ScaleY, 1);
}
