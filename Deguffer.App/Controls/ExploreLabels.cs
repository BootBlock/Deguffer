using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The names laid over the map, as real text rather than as pixels in the picture.
///
/// <para>Text controls, so they scale with the user's text size and a screen reader could reach
/// them — neither of which a name burnt into the picture offers. There are only ever a few dozen,
/// because a shape too small to read is a shape the drawing gives no label.</para>
///
/// <para>Each name is placed by the rule its drawing placed it by (<see cref="ExploreLabel.Across"/> and
/// <see cref="ExploreLabel.Down"/>), and the compositor works that rule out again on every frame as
/// the map's camera moves and as a folder opening carries the picture, so the names stay on the map
/// through every zoom, drag and folder opened with no work on the UI thread per frame. A zoom ends
/// with each name already where the drawing at the new zoom puts it, rather than jumping there.
/// Only the anchor moves: the text stays upright and at the reader's size, so it fades out as its
/// shape shrinks round it, before it reaches a shape that is not its own
/// (<see cref="ExploreLabel.Fade"/>), and a shape that grows keeps its name.</para>
///
/// <para>Separate from <see cref="ExploreMap"/> for the reason <see cref="ExploreHighlight"/> is.
/// That one is about which tree is drawn and what the pointer found; this is about putting a few
/// dozen pieces of text where somebody else said they go (G1).</para>
/// </summary>
internal sealed class ExploreLabels : Canvas
{
    /// <summary>
    /// A name's properties, which the expressions below read: the rule it is placed across its
    /// drawing by, as the span's start and end, the inset, and 1 where it is centred on the span
    /// (<see cref="LabelAxis"/>).
    /// </summary>
    private const string Across = "Across";

    /// <summary>The rule it is placed down its drawing by, as <see cref="Across"/>.</summary>
    private const string Down = "Down";

    /// <summary>1 on each axis where the span is cut to the canvas first, and 0 where it is not.</summary>
    private const string Kept = "Kept";

    /// <summary>The canvas's size on the screen, in device-independent pixels: where a kept span is cut.</summary>
    private const string Extent = "Extent";

    /// <summary>How many device pixels a device-independent one is: what an anchor is rounded to.</summary>
    private const string Pixel = "Pixel";

    /// <summary>How far left of its anchor the text starts, so a centred name is centred on it.</summary>
    private const string Shift = "Shift";

    /// <summary>How far above its anchor the text starts, so a name of several lines is centred down it.</summary>
    private const string Lift = "Lift";

    /// <summary>The magnifications it fades between, across then down: see <see cref="ExploreLabel.Fade"/>.</summary>
    private const string Fade = "Fade";

    /// <summary>How far it has arrived, from 0 to 1.</summary>
    private const string Arrival = "Arrival";

    /// <summary>
    /// Where a name's anchor is on the screen: its rule (<see cref="LabelAxis.At"/>) worked out for
    /// where its drawing lies in the picture and where the camera puts the picture, then carried
    /// wherever a folder opening carries it. Each a scale then a move, written out because the
    /// expression language composes nothing for itself.
    /// </summary>
    private static readonly string PlacementExpression =
        "Vector3("
        + $"({Pinned(Across, "X")}) * carried.Scale.X + carried.Offset.X - label.{Shift}, "
        + $"({Pinned(Down, "Y")}) * carried.Scale.Y + carried.Offset.Y - label.{Lift}, "
        + "0)";

    /// <summary>
    /// How opaque a name is: by how far its picture is magnified on each axis against its fade, by how
    /// far it has arrived, and by how far the picture it names has.
    /// </summary>
    private const string OpacityExpression =
        "Clamp(Min("
        + $"((placed.Scale.X * camera.Scale.X * carried.Scale.X) - label.{Fade}.X) / (label.{Fade}.Y - label.{Fade}.X), "
        + $"((placed.Scale.Y * camera.Scale.Y * carried.Scale.Y) - label.{Fade}.Z) / (label.{Fade}.W - label.{Fade}.Z)), 0, 1)"
        + $" * label.{Arrival} * carried.Opacity";

    private readonly Compositor _compositor;

    private readonly MapCamera _camera;

    private readonly IMotionPolicy _motion;

    /// <summary>
    /// Where the drawing the names were laid out for lies in the picture, per device-independent
    /// pixel of that drawing: a <c>Scale</c> and an <c>Offset</c>, combined with the camera's by the
    /// compositor.
    /// </summary>
    private readonly CompositionPropertySet _placed;

    /// <summary>
    /// Where one device-independent pixel of that drawing is on the screen: <see cref="_placed"/> and
    /// the camera combined, as a <c>Scale</c> and an <c>Offset</c>, by expressions of its own. Worked
    /// out once here for every name, because the compositor refuses an expression as long as each
    /// name's would be with this written into it four times.
    /// </summary>
    private readonly CompositionPropertySet _screen;

    /// <summary>One entry per text block, in the same order: what moves and fades it.</summary>
    private readonly List<Label> _names = [];

    /// <summary>
    /// The nodes named on the map now, in <see cref="_tree"/>, so the next drawing of the same tree can
    /// tell which of its names are new. Swapped with <see cref="_naming"/> rather than rebuilt (G5).
    /// </summary>
    private HashSet<int> _named = [];

    private HashSet<int> _naming = [];

    private ISizedTree? _tree;

    /// <summary>What a folder opening carries the picture by: the visual over the drawings the names follow.</summary>
    private Visual _carried;

    /// <summary>
    /// Names that move with <paramref name="camera"/>, wherever <see cref="Place"/> puts them in the
    /// picture, and with <paramref name="carried"/> as a folder opening carries the picture.
    /// </summary>
    public ExploreLabels(MapCamera camera, Visual carried, IMotionPolicy motion)
    {
        // Never the thing being clicked. A click that landed on a name rather than the shape under
        // it would select whatever that name happened to overlap.
        IsHitTestVisible = false;

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _camera = camera;
        _carried = carried;
        _motion = motion;

        // A name in SpaceMonger's lettering is drawn into an image, whose contents a device lost or
        // replaced takes with it.
        Loaded += (_, _) => CompositionTarget.SurfaceContentsLost += OnContentsLost;
        Unloaded += (_, _) => CompositionTarget.SurfaceContentsLost -= OnContentsLost;

        _placed = _compositor.CreatePropertySet();
        _placed.InsertVector3(nameof(Visual.Scale), Vector3.One);
        _placed.InsertVector3(nameof(Visual.Offset), Vector3.Zero);
        _placed.InsertVector2(Extent, Vector2.Zero);
        _placed.InsertScalar(Pixel, 1);

        _screen = _compositor.CreatePropertySet();
        _screen.InsertVector3(nameof(Visual.Scale), Vector3.One);
        _screen.InsertVector3(nameof(Visual.Offset), Vector3.Zero);
        Combine(nameof(Visual.Scale), "Vector3(placed.Scale.X * camera.Scale.X, placed.Scale.Y * camera.Scale.Y, 1)");
        Combine(
            nameof(Visual.Offset),
            "Vector3((placed.Offset.X * camera.Scale.X) + camera.Offset.X, (placed.Offset.Y * camera.Scale.Y) + camera.Offset.Y, 0)");
    }

    private void Combine(string property, string expression)
    {
        var combined = _compositor.CreateExpressionAnimation(expression);
        combined.SetReferenceParameter("placed", _placed);
        combined.SetReferenceParameter("camera", _camera.Properties);
        _screen.StartAnimation(property, combined);
    }

    /// <summary>
    /// Put the names where <paramref name="placed"/> says one device-independent pixel of the drawing
    /// they name lies in the picture, on a canvas <paramref name="width"/> by <paramref name="height"/>
    /// device-independent pixels.
    /// </summary>
    public void Place(MapTransform placed, double width, double height)
    {
        _placed.Place(placed);
        _placed.InsertVector2(Extent, new Vector2((float)width, (float)height));
    }

    /// <summary>
    /// Follow <paramref name="carried"/> as a folder opening carries the picture, for the set of
    /// drawings the names are of now. The map swaps its two sets as a folder opens.
    /// </summary>
    public void Ride(Visual carried)
    {
        if (carried == _carried)
        {
            return;
        }

        _carried = carried;

        foreach (var name in _names)
        {
            name.Follow(carried);
        }
    }

    /// <summary>
    /// Put <paramref name="caption"/>'s text on each shape <paramref name="drawing"/> chose to label,
    /// at <paramref name="scale"/> canvas pixels to the device-independent pixel.
    ///
    /// <para>The caption comes from the page rather than from here, because only it knows what the
    /// tree's nodes are: a drive's are a file name and a size, and a memory picture's are a process or
    /// a part of Windows. How it is written is the drawing's lettering (<see cref="MapCaption.Text"/>).</para>
    ///
    /// <para>The text blocks are kept and written over rather than rebuilt. A scan repaints this
    /// several times a second, and each rebuild would throw away a few dozen controls and their
    /// brushes and make the framework measure and arrange a fresh set of them, for text that has
    /// usually not changed (G5).</para>
    ///
    /// <para>A name the map was not showing fades in, where the drawing is of the tree it was showing:
    /// detail a zoom or a folder opened uncovered. Another tree is a scan's next snapshot or another
    /// picture altogether, whose nodes are not the same nodes, and its names are put up at once, as a
    /// scan repainting several times a second needs.</para>
    /// </summary>
    public void Show(ExploreSurface drawing, double scale, Func<ExploreLabel, MapCaption> caption)
    {
        Visibility = Visibility.Visible;
        _placed.InsertScalar(Pixel, (float)scale);

        while (Children.Count < drawing.Labels.Count)
        {
            var name = new Label(_compositor, _camera.Properties, _placed, _screen, _carried);

            _names.Add(name);
            Children.Add(name.Text.Element);
        }

        while (Children.Count > drawing.Labels.Count)
        {
            Children.RemoveAt(Children.Count - 1);
            _names.RemoveAt(_names.Count - 1);
        }

        var sameTree = ReferenceEquals(drawing.Tree, _tree);
        var arrival = _motion.For(MotionToken.Detail);

        _naming.Clear();

        for (var i = 0; i < drawing.Labels.Count; i++)
        {
            var label = drawing.Labels[i];
            var name = _names[i];
            var measured = name.Text.Write(
                caption(label).Text(drawing.Lettering, label.Lines),
                drawing.Lettering,
                label.Width / scale,
                label.Centred,
                label.Colour.ToColor(),
                label.Rotation,
                drawing.LineSpacing / scale,
                scale);
            var (across, down) = label.Fade((float)(measured.Width * scale), (float)(measured.Height * scale));
            name.Properties.InsertVector4(Across, Rule(label.Across, scale));
            name.Properties.InsertVector4(Down, Rule(label.Down, scale));
            name.Properties.InsertVector2(Kept, new Vector2(label.Across.KeptOnCanvas ? 1 : 0, label.Down.KeptOnCanvas ? 1 : 0));
            name.Properties.InsertScalar(Shift, label.Centred ? Half(measured.Width, scale) : 0);
            name.Properties.InsertScalar(Lift, label.Middle ? Half(measured.Height, scale) : 0);
            name.Properties.InsertVector4(Fade, new Vector4(across.Gone, across.Whole, down.Gone, down.Whole));

            if (!sameTree || _named.Contains(label.Node))
            {
                // Still arriving where it was already this node's name.
                if (!(sameTree && name.Node == label.Node))
                {
                    name.Arrive();
                }
            }
            else
            {
                name.Enter(arrival, _compositor.MotionEase());
            }

            name.Node = label.Node;
            _naming.Add(label.Node);
        }

        (_named, _naming) = (_naming, _named);
        _tree = drawing.Tree;
    }

    private void OnContentsLost(object? sender, object e)
    {
        foreach (var name in _names)
        {
            name.Text.Redraw();
        }
    }

    /// <summary>
    /// Half of <paramref name="length"/> device-independent pixels, down to a whole device pixel, so a
    /// centred name starts on one as its anchor does (see <see cref="Pinned"/>).
    /// </summary>
    private static float Half(double length, double scale) => (float)(Math.Floor(length * scale / 2) / scale);

    /// <summary>A rule as the expressions read it, in device-independent pixels of a canvas at <paramref name="scale"/>.</summary>
    private static Vector4 Rule(LabelAxis axis, double scale) => new(
        (float)(axis.From / scale),
        (float)(axis.To / scale),
        (float)(axis.Inset / scale),
        axis.Centred ? 1 : 0);

    /// <summary>
    /// <see cref="LabelAxis.At"/> as an expression, along <paramref name="axis"/> for the rule in the
    /// name's <paramref name="rule"/> property: its span carried into the picture and onto the screen,
    /// cut to the canvas where it is kept, and the anchor measured from its start or its middle.
    /// </summary>
    private static string Pinned(string rule, string axis)
    {
        var from = OnScreen($"label.{rule}.X", axis);
        var to = OnScreen($"label.{rule}.Y", axis);
        var kept = $"label.{Kept}.{axis}";
        var start = $"Lerp({from}, Max({from}, 0), {kept})";
        var end = $"Lerp({to}, Min({to}, placed.{Extent}.{axis}), {kept})";
        var centred = $"label.{rule}.W";

        // The start, or the middle of the span, as one sum: start + (end - start) / 2 where centred.
        // Rounded to a whole device pixel, because a name drawn off the pixel grid is resampled, and
        // one in SpaceMonger's unsmoothed lettering comes out smoothed after all; the middle of a span
        // falls on a half pixel as often as not.
        var anchor = $"({start} * (1 - ({centred} / 2))) + ({end} * ({centred} / 2)) + label.{rule}.Z";

        return $"Floor(({anchor}) * placed.{Pixel} + 0.5) / placed.{Pixel}";
    }

    /// <summary>A point of a drawing in its device-independent pixels, on the screen.</summary>
    private static string OnScreen(string value, string axis) =>
        $"(({value} * screen.Scale.{axis}) + screen.Offset.{axis})";

    /// <summary>
    /// Take the names off until the layout that places them arrives.
    ///
    /// <para>For while a resize settles, a folder opens out of the picture they name, or the device
    /// was replaced. A resize stretches the picture on each axis apart, and a name the layout gave room
    /// to at one size can run over its neighbours at another.</para>
    /// </summary>
    public void Hide() => Visibility = Visibility.Collapsed;

    /// <summary>
    /// Put them back without redrawing them, for a caller that has dropped the redraw which would
    /// have. The positions are still the ones the last drawing gave, because a size that was never
    /// drawn never moved them.
    /// </summary>
    public void Reveal() => Visibility = Visibility.Visible;

    /// <summary>Take them off for good, for a map that is no longer showing anything.</summary>
    public void Clear()
    {
        Children.Clear();
        _names.Clear();
        _named.Clear();
        _tree = null;
    }

    /// <summary>
    /// One reusable piece of label text, with everything a repaint never changes already set —
    /// including the brush and the transform, which are written through rather than replaced — and
    /// what moves and fades it on the compositor.
    /// </summary>
    private sealed class Label
    {
        private readonly ExpressionAnimation _placement;

        private readonly ExpressionAnimation _opacity;

        private readonly Visual _visual;

        public Label(
            Compositor compositor,
            CompositionPropertySet camera,
            CompositionPropertySet placed,
            CompositionPropertySet screen,
            Visual carried)
        {
            Text = new LabelText();

            Properties = compositor.CreatePropertySet();
            Properties.InsertVector4(Across, Vector4.Zero);
            Properties.InsertVector4(Down, Vector4.Zero);
            Properties.InsertVector2(Kept, Vector2.Zero);
            Properties.InsertScalar(Shift, 0);
            Properties.InsertScalar(Lift, 0);
            Properties.InsertVector4(Fade, new Vector4(0, 1, 0, 1));
            Properties.InsertScalar(Arrival, 1);

            _placement = compositor.CreateExpressionAnimation(PlacementExpression);
            _opacity = compositor.CreateExpressionAnimation(OpacityExpression);

            foreach (var expression in new[] { _placement, _opacity })
            {
                expression.SetReferenceParameter("label", Properties);
                expression.SetReferenceParameter("camera", camera);
                expression.SetReferenceParameter("placed", placed);
                expression.SetReferenceParameter("screen", screen);
            }

            _visual = ElementCompositionPreview.GetElementVisual(Text.Element);
            ElementCompositionPreview.SetIsTranslationEnabled(Text.Element, true);


            Follow(carried);
        }

        /// <summary>The text of the name, in the drawing's lettering.</summary>
        public LabelText Text { get; }

        /// <summary>
        /// Where the name is and how it fades, in its own coordinates: <c>Across</c>, <c>Down</c>,
        /// <c>Kept</c>, <c>Shift</c>, <c>Lift</c>, <c>Fade</c> and <c>Arrival</c>.
        /// </summary>
        public CompositionPropertySet Properties { get; }

        /// <summary>The node this names, so a repaint naming it again lets an arrival run on.</summary>
        public int Node { get; set; } = -1;

        /// <summary>Move and fade with <paramref name="carried"/>, from now on.</summary>
        public void Follow(Visual carried)
        {
            foreach (var expression in new[] { _placement, _opacity })
            {
                expression.SetReferenceParameter("carried", carried);
            }

            _visual.StartAnimation(VisualTranslation.Property, _placement);
            _visual.StartAnimation(nameof(Visual.Opacity), _opacity);
        }

        /// <summary>Be there at once.</summary>
        public void Arrive()
        {
            Properties.StopAnimation(Arrival);
            Properties.InsertScalar(Arrival, 1);
        }

        /// <summary>Fade in, as <paramref name="played"/> says, along <paramref name="ease"/>.</summary>
        public void Enter(Motion played, CompositionEasingFunction ease)
        {
            if (played.IsInstant)
            {
                Arrive();
                return;
            }

            var fade = Properties.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, ease);
            fade.Duration = played.Duration;

            Properties.StartAnimation(Arrival, fade);
        }
    }
}
