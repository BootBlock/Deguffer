using System.Globalization;
using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Interactions;

namespace Deguffer.App.Controls;

/// <summary>
/// Where a map's whole picture is on the screen: one scale and one offset, in a property set the
/// compositor reads, and every part of the picture follows by an expression.
///
/// <para>One authority for the placement. The drawings, the outlines and the names are each placed
/// in the picture once, and this places the picture, so they move together by construction rather
/// than by the same transform being copied to each of them at every frame.</para>
///
/// <para>The scale and the offset are themselves expressions of the map's interaction tracker (see
/// <see cref="ExploreZoom"/>), so a pinch, a fling or a glide moves the picture on the compositor
/// without the UI thread writing anything at all.</para>
/// </summary>
internal sealed class MapCamera
{
    /// <summary>The property holding how far a drag has pulled the picture past the tracker's bounds.</summary>
    private const string Stretched = "Stretch";

    private readonly Compositor _compositor;

    /// <summary>The batch a spring back is played in, until it ends, so a press can tell it is on.</summary>
    private CompositionScopedBatch? _springing;

    private readonly ExpressionAnimation _scale;

    private readonly ExpressionAnimation _offset;

    public MapCamera(Compositor compositor)
    {
        _compositor = compositor;

        Properties = compositor.CreatePropertySet();
        Properties.InsertVector3(nameof(Visual.Scale), Vector3.One);
        Properties.InsertVector3(nameof(Visual.Offset), Vector3.Zero);
        Properties.InsertVector2(nameof(Visual.Size), Vector2.Zero);
        Properties.InsertVector3(Stretched, Vector3.Zero);

        _scale = compositor.CreateExpressionAnimation("camera.Scale");
        _scale.SetReferenceParameter("camera", Properties);
        _offset = compositor.CreateExpressionAnimation("camera.Offset");
        _offset.SetReferenceParameter("camera", Properties);
    }

    /// <summary>
    /// The camera's <c>Scale</c> and <c>Offset</c>, for an expression that combines them with
    /// something else, as the names over the picture do; and the map's <c>Size</c>, which the
    /// tracker's bounds are worked out from.
    /// </summary>
    public CompositionPropertySet Properties { get; }

    /// <summary>The map is <paramref name="width"/> by <paramref name="height"/> now.</summary>
    public void Resize(double width, double height) =>
        Properties.InsertVector2(nameof(Visual.Size), new Vector2((float)width, (float)height));

    /// <summary>
    /// Put the whole picture where <paramref name="tracker"/> holds it, from now on.
    ///
    /// <para>Where the picture may not be stretched past its limits, because the reader has turned
    /// animation effects off, the camera follows the tracker held to them: the tracker itself still
    /// gives a little under a hand and settles back, and a camera following it as it is would show
    /// that as a spring. The arithmetic is <see cref="MapTracking.Shown"/>'s, which is what a click is
    /// resolved through, so the two must stay the same.</para>
    /// </summary>
    public void Track(InteractionTracker tracker, bool elastic)
    {
        var scale = elastic
            ? "tracker.Scale"
            : string.Create(CultureInfo.InvariantCulture, $"Clamp(tracker.Scale, 1, {MapViewport.MaximumZoom})");

        var offset = elastic
            ? $"-tracker.Position + camera.{Stretched}"
            : $"-Vector3(Clamp(tracker.Position.X, 0, ({scale} - 1) * camera.Size.X), "
                + $"Clamp(tracker.Position.Y, 0, ({scale} - 1) * camera.Size.Y), 0)";

        Properties.StartAnimation(nameof(Visual.Scale), Expression($"Vector3({scale}, {scale}, 1)", tracker));
        Properties.StartAnimation(nameof(Visual.Offset), Expression(offset, tracker));
    }

    /// <summary>
    /// Show the picture pulled (<paramref name="x"/>, <paramref name="y"/>) pixels further than the
    /// tracker holds it, for a drag past the picture's edge.
    ///
    /// <para>The camera's rather than the tracker's, because the tracker keeps its position inside its
    /// bounds while it is asked for one outside them: a position placed past them goes on showing
    /// after the tracker has reported it back inside, and nothing it is then asked brings it back.</para>
    /// </summary>
    public void Stretch(double x, double y)
    {
        Unstretch();
        Properties.InsertVector3(Stretched, new Vector3((float)x, (float)y, 0));
    }

    /// <summary>
    /// Ease a stretch back to nothing as <paramref name="motion"/> says, and call
    /// <paramref name="done"/> once it has, unless a press stopped it first.
    /// </summary>
    public void SpringBack(Motion motion, Action done)
    {
        var (first, second) = Motion.EaseControlPoints;
        var back = _compositor.CreateVector3KeyFrameAnimation();
        back.InsertKeyFrame(1, Vector3.Zero, _compositor.CreateCubicBezierEasingFunction(first, second));
        back.Duration = motion.Duration;

        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Properties.StartAnimation(Stretched, back);
        batch.End();

        _springing = batch;
        batch.Completed += (_, _) =>
        {
            if (_springing != batch)
            {
                return;
            }

            _springing = null;
            done();
        };
    }

    /// <summary>Take any stretch away at once. Says whether that stopped a spring back on its way.</summary>
    public bool Unstretch()
    {
        var springing = _springing is not null;

        _springing = null;
        Properties.StopAnimation(Stretched);
        Properties.InsertVector3(Stretched, Vector3.Zero);

        return springing;
    }

    /// <summary>Move <paramref name="visual"/> with the camera, on the compositor, from now on.</summary>
    public void Follow(Visual visual)
    {
        visual.StartAnimation(nameof(Visual.Scale), _scale);
        visual.StartAnimation(nameof(Visual.Offset), _offset);
    }

    /// <summary>
    /// Stop <paramref name="visual"/> following the camera, and leave it where
    /// <paramref name="camera"/> put it.
    /// </summary>
    public static void Freeze(Visual visual, MapTransform camera)
    {
        visual.StopAnimation(nameof(Visual.Scale));
        visual.StopAnimation(nameof(Visual.Offset));

        visual.Scale = new Vector3((float)camera.ScaleX, (float)camera.ScaleY, 1);
        visual.Offset = new Vector3((float)camera.X, (float)camera.Y, 0);
    }

    private ExpressionAnimation Expression(string expression, InteractionTracker tracker)
    {
        var animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("tracker", tracker);
        animation.SetReferenceParameter("camera", Properties);

        return animation;
    }
}
