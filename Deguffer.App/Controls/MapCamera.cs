using System.Numerics;
using Deguffer.App.Shell;
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

    /// <summary>The camera's origin, as fractions of the picture (see <see cref="MapOrigin"/>).</summary>
    private const string Near = "Near";

    /// <summary>What is left of the picture past the camera's origin, as fractions of it.</summary>
    private const string Far = "Far";

    /// <summary><see cref="Near"/> and <see cref="Far"/> for the origin before, which the tracker's bounds still take in while the origin moves.</summary>
    private const string FormerNear = "FormerNear";

    private const string FormerFar = "FormerFar";

    /// <summary>How far the picture can be magnified while the drawing on screen is (see <see cref="MapCeiling"/>).</summary>
    private const string Ceiling = "Ceiling";

    /// <summary>The least magnification <see cref="Unmagnified"/> divides by, so a picture shrunk to nothing divides by no zero.</summary>
    private const string LeastMagnification = "0.0001";

    /// <summary>
    /// How many times the <see cref="Ceiling"/> the bounds pinned at rest are worked out for (see
    /// <see cref="PinBounds"/>). The tracker can rest at the ceiling against an edge, where bounds for
    /// exactly that scale, worked out here in double precision, could fall a rounding error inside where
    /// the compositor held it in single precision. Twice the scale puts the far bound a screen or more
    /// past it, since the origin is always a screen's corner, and doubles the near one.
    /// </summary>
    private const double PinnedScales = 2;

    private readonly Compositor _compositor;

    /// <summary>The batch a spring back is played in, until it ends, so a press can tell it is on.</summary>
    private CompositionScopedBatch? _springing;

    /// <summary>The origin the camera is measured from, which the tracker's bounds settle on.</summary>
    private MapOrigin _origin;

    /// <summary>The origin before, which the tracker's bounds still take in until <see cref="Settle"/>.</summary>
    private MapOrigin _former;

    /// <summary>The map's size and the zoom's ceiling, as the property set has them, for the bounds pinned at rest.</summary>
    private (double Width, double Height) _size;

    private double _ceiling;

    /// <summary>The tracker <see cref="Bound"/> keeps inside the picture, and its bounds as expressions of its scale.</summary>
    private (InteractionTracker Tracker, ExpressionAnimation Least, ExpressionAnimation Most)? _bounds;

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
        Limit(MapCeiling.Least);
        Rebase(default);
        Settle();

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

    /// <summary>
    /// Whether the tracker's bounds are pinned for a tracker at rest, rather than following its scale.
    /// See <see cref="PinBounds"/>.
    /// </summary>
    public bool BoundsPinned { get; private set; }

    /// <summary>Hand the expressions the map's size, which the tracker's bounds are worked out from.</summary>
    public void Resize(double width, double height)
    {
        _size = (width, height);
        Properties.InsertVector2(nameof(Visual.Size), new Vector2((float)width, (float)height));
        Repin();
    }

    /// <summary>
    /// Hold the zoom to <paramref name="ceiling"/> from now on, where the camera holds it at all: see
    /// <see cref="Track"/>. Read by the expression rather than written into it, so the ceiling moves
    /// with each drawing without the camera being started again.
    /// </summary>
    public void Limit(double ceiling)
    {
        _ceiling = ceiling;
        Properties.InsertScalar(Ceiling, (float)ceiling);
        Repin();
    }

    /// <summary>
    /// Put the whole picture where <paramref name="tracker"/> holds it, from now on.
    ///
    /// <para>Where the picture may not be stretched past its limits, because the reader has turned
    /// animation effects off, the camera follows the tracker held to them: the tracker itself still
    /// gives a little under a hand and settles back, and a camera following it as it is would show
    /// that as a spring. Held by <see cref="MapTracking.HeldScale{T}"/> and
    /// <see cref="MapTracking.HeldPosition{T}"/>, the rules <see cref="MapTracking.Shown"/> holds a
    /// click's camera by.</para>
    /// </summary>
    public void Track(InteractionTracker tracker, bool elastic)
    {
        var scale = elastic ? TrackerScale : MapTracking.HeldScale(TrackerScale, Camera(Ceiling));

        var offset = elastic
            ? $"-tracker.Position + camera.{Stretched}"
            : $"-Vector3({Held("X", scale)}, {Held("Y", scale)}, 0)";

        Properties.StartAnimation(nameof(Visual.Scale), Expression($"Vector3({scale}, {scale}, 1)", tracker));
        Properties.StartAnimation(nameof(Visual.Offset), Expression(offset, tracker));

        static ExpressionScalar Held(string axis, ExpressionScalar scale) => MapTracking.HeldPosition(
            new ExpressionScalar($"tracker.Position.{axis}"),
            Camera($"{Near}.{axis}"),
            Camera($"{Far}.{axis}"),
            scale,
            Camera($"Size.{axis}"));
    }

    /// <summary>
    /// Keep <paramref name="tracker"/> inside the picture from now on, at whatever scale it is: its
    /// position goes from the picture's near edges to where its far edges meet the screen's, measured
    /// from the camera's origin, by <see cref="MapOrigin.Least{T}"/> and <see cref="MapOrigin.Most{T}"/>.
    /// It starts at rest, with the bounds pinned (see <see cref="PinBounds"/>).
    ///
    /// <para>While the origin moves the bounds take in both the old origin's and the new one's, so the
    /// tracker is inside them whichever of the two it is measured from at that moment: the bounds and
    /// the request that moves the tracker to the new origin reach the compositor separately, and a
    /// tracker found outside its bounds springs back on its own.</para>
    /// </summary>
    public void Bound(InteractionTracker tracker)
    {
        var least = Expression($"Vector3({Least("X")}, {Least("Y")}, 0)", tracker);
        var most = Expression($"Vector3({Most("X")}, {Most("Y")}, 0)", tracker);

        _bounds = (tracker, least, most);
        PinBounds();

        static ExpressionScalar Least(string axis) => ExpressionScalar.Min(
            MapOrigin.Least(Camera($"{Near}.{axis}"), TrackerScale, Camera($"Size.{axis}")),
            MapOrigin.Least(Camera($"{FormerNear}.{axis}"), TrackerScale, Camera($"Size.{axis}")));

        static ExpressionScalar Most(string axis) => ExpressionScalar.Max(
            MapOrigin.Most(Camera($"{Near}.{axis}"), Camera($"{Far}.{axis}"), TrackerScale, Camera($"Size.{axis}")),
            MapOrigin.Most(Camera($"{FormerNear}.{axis}"), Camera($"{FormerFar}.{axis}"), TrackerScale, Camera($"Size.{axis}")));
    }

    /// <summary>
    /// The tracker is at rest: its bounds stop following its scale, and hold every position it can take
    /// at any scale up to <see cref="PinnedScales"/> times the ceiling, until <see cref="UnpinBounds"/>.
    /// A change to what they are worked out from pins them again where it is.
    ///
    /// <para>Bounds that are an expression of the tracker's own scale have the compositor work the
    /// tracker out again at every frame, and with it everything that reads it: the camera, and every
    /// name, outline and frame that follows the camera. Nothing ever stops that while the expressions
    /// run, off screen and minimised included. Pinned wider than any scale the tracker rests at, the
    /// bounds never move a tracker at rest, and they leave the first frames of a hand that takes it,
    /// before <see cref="UnpinBounds"/> reaches the compositor, free rather than held by the wrong
    /// scale.</para>
    /// </summary>
    public void PinBounds()
    {
        if (BoundsPinned || _bounds is not { } bounds)
        {
            return;
        }

        bounds.Tracker.StopAnimation(nameof(InteractionTracker.MinPosition));
        bounds.Tracker.StopAnimation(nameof(InteractionTracker.MaxPosition));
        BoundsPinned = true;
        Repin();
    }

    /// <summary>
    /// The tracker is about to move by itself, under a hand or through a glide or a fling: its bounds
    /// follow its scale again, until <see cref="PinBounds"/>.
    /// </summary>
    public void UnpinBounds()
    {
        if (!BoundsPinned || _bounds is not { } bounds)
        {
            return;
        }

        BoundsPinned = false;
        bounds.Tracker.StartAnimation(nameof(InteractionTracker.MinPosition), bounds.Least);
        bounds.Tracker.StartAnimation(nameof(InteractionTracker.MaxPosition), bounds.Most);
    }

    /// <summary>Write the pinned bounds afresh from what they are worked out from, where they are pinned.</summary>
    private void Repin()
    {
        if (!BoundsPinned || _bounds is not { } bounds)
        {
            return;
        }

        var scale = PinnedScales * _ceiling;
        var across = Widest(_origin.Across(scale, _size.Width), _former.Across(scale, _size.Width));
        var down = Widest(_origin.Down(scale, _size.Height), _former.Down(scale, _size.Height));

        bounds.Tracker.MinPosition = new Vector3((float)across.Least, (float)down.Least, 0);
        bounds.Tracker.MaxPosition = new Vector3((float)across.Most, (float)down.Most, 0);

        static (double Least, double Most) Widest((double Least, double Most) one, (double Least, double Most) other) =>
            (Math.Min(one.Least, other.Least), Math.Max(one.Most, other.Most));
    }

    /// <summary>
    /// Measure from <paramref name="origin"/> from now on. The tracker's bounds still take in the
    /// origin before it until <see cref="Settle"/>.
    /// </summary>
    public void Rebase(MapOrigin origin)
    {
        _origin = origin;
        Measure(Near, Far, origin);
        Repin();
    }

    /// <summary>The tracker is measured from the camera's origin now: its bounds are that origin's alone.</summary>
    public void Settle()
    {
        _former = _origin;
        Measure(FormerNear, FormerFar, _origin);
        Repin();
    }

    /// <summary>
    /// Write <paramref name="origin"/> into <paramref name="near"/>, and what is left of the picture
    /// past it into <paramref name="far"/>. The second is worked out here, in double precision: near
    /// the picture's far edge it is small, and the expressions multiply it by the scale.
    /// </summary>
    private void Measure(string near, string far, MapOrigin origin)
    {
        Properties.InsertVector2(near, new Vector2((float)origin.Left, (float)origin.Top));
        Properties.InsertVector2(far, new Vector2((float)(1 - origin.Left), (float)(1 - origin.Top)));
    }

    /// <summary>
    /// Stop following the tracker and stay at <paramref name="camera"/>, until <see cref="Track"/>
    /// again. For the moment the origin moves: the camera and every placement change together on the
    /// UI thread, and the tracker follows a frame or more later.
    /// </summary>
    public void Hold(MapTransform camera)
    {
        Properties.StopAnimation(nameof(Visual.Scale));
        Properties.StopAnimation(nameof(Visual.Offset));
        Properties.Place(camera);
    }

    /// <summary>
    /// An expression for where the screen's near edge on <paramref name="axis"/>, X or Y, falls in the
    /// whole picture, as a fraction of it, with these <see cref="Properties"/> as <c>camera</c>: the
    /// camera read back as a viewport, by <see cref="MapTracking.ShownEdge{T}"/>, the rule
    /// <see cref="MapTracking.Shown"/> reads the tracker by. The tracker's position is the camera's
    /// offset negated, measured from the origin, so it stays exact at any zoom.
    /// </summary>
    public static ExpressionScalar ShownEdge(string axis) => MapTracking.ShownEdge(
        Camera($"{Near}.{axis}"),
        -Camera($"Offset.{axis}"),
        Camera($"Scale.{axis}"),
        Camera($"Size.{axis}"));

    /// <summary>
    /// An animation of <paramref name="length"/> device-independent pixels on screen, for a length
    /// inside a canvas that <paramref name="placed"/> places in the picture, whatever the camera and
    /// that placement magnify it by: divided by both, at every frame of a move.
    ///
    /// <para>Divided by the larger of the two axes, which makes the length an upper bound: the two
    /// differ only while a resize settles, and an outline briefly a shade thin reads better than one
    /// briefly heavy, which is what swamps a shape a few pixels across.</para>
    /// </summary>
    public ExpressionAnimation Unmagnified(float length, CompositionObject placed) =>
        Unmagnifying($"Length / Max({LeastMagnification}, Max(camera.Scale.X * placed.Scale.X, camera.Scale.Y * placed.Scale.Y))", length, placed);

    /// <summary>
    /// As <see cref="Unmagnified"/>, for a move <paramref name="length"/> straight down the screen: a
    /// <c>Vector3</c>, divided by the magnification down alone.
    /// </summary>
    public ExpressionAnimation UnmagnifiedDown(float length, CompositionObject placed) =>
        Unmagnifying($"Vector3(0, Length / Max({LeastMagnification}, camera.Scale.Y * placed.Scale.Y), 0)", length, placed);

    private ExpressionAnimation Unmagnifying(string expression, float length, CompositionObject placed)
    {
        var animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetScalarParameter("Length", length);
        animation.SetReferenceParameter("camera", Properties);
        animation.SetReferenceParameter("placed", placed);

        return animation;
    }

    /// <summary>The tracker's own scale, unheld.</summary>
    private static ExpressionScalar TrackerScale => new("tracker.Scale");

    /// <summary>A member of these <see cref="Properties"/>, read as <c>camera</c>.</summary>
    private static ExpressionScalar Camera(string member) => new($"camera.{member}");

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
    ///
    /// <para>An instant motion takes the stretch away at once and calls <paramref name="done"/> before
    /// this returns. The compositor refuses a key frame animation shorter than a millisecond, and a
    /// reader with animation effects off reaches this whenever a drag held at an edge rounds a hair
    /// past it.</para>
    /// </summary>
    public void SpringBack(Motion motion, Action done)
    {
        if (motion.IsInstant)
        {
            Unstretch();
            done();
            return;
        }

        var back = _compositor.CreateVector3KeyFrameAnimation();
        back.InsertKeyFrame(1, Vector3.Zero, _compositor.MotionEase());
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

        visual.Place(camera);
    }

    private ExpressionAnimation Expression(string expression, InteractionTracker tracker)
    {
        var animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("tracker", tracker);
        animation.SetReferenceParameter("camera", Properties);

        return animation;
    }
}
