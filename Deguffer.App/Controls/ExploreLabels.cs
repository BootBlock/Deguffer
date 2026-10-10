using System.Numerics;
using Deguffer.App.Shell;
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
/// <para>Each name is placed in the picture at its anchor (<see cref="ExploreLabel.Anchor"/>), and the
/// compositor moves it there with the map's camera and with the picture it names as a folder opens,
/// so the names stay on the map through every zoom, drag and folder opened with no work on the UI
/// thread per frame. Only the anchor moves: the text stays upright and at the reader's size, so it
/// fades out as its shape shrinks round it, before it reaches a shape that is not its own
/// (<see cref="ExploreLabel.Fade"/>), and a shape that grows keeps its name.</para>
///
/// <para>Separate from <see cref="ExploreMap"/> for the reason <see cref="ExploreHighlight"/> is.
/// That one is about which tree is drawn and what the pointer found; this is about putting a few
/// dozen pieces of text where somebody else said they go (G1).</para>
/// </summary>
internal sealed class ExploreLabels : Canvas
{
    /// <summary>
    /// Where a name's anchor is on the screen, as the composition of where its drawing lies in the
    /// picture, where the camera puts the picture, and where a folder opening carries it: each a
    /// scale then a move, written out because the expression language composes nothing for itself.
    /// </summary>
    private const string PlacementExpression =
        "Vector3("
        + "((((label.Anchor.X * placed.Scale.X) + placed.Offset.X) * camera.Scale.X) + camera.Offset.X) * carried.Scale.X + carried.Offset.X - label.Shift, "
        + "((((label.Anchor.Y * placed.Scale.Y) + placed.Offset.Y) * camera.Scale.Y) + camera.Offset.Y) * carried.Scale.Y + carried.Offset.Y, "
        + "0)";

    /// <summary>
    /// How opaque a name is: by how far its picture is magnified on each axis against its fade, by how
    /// far it has arrived, and by how far the picture it names has.
    /// </summary>
    private const string OpacityExpression =
        "Clamp(Min("
        + "((placed.Scale.X * camera.Scale.X * carried.Scale.X) - label.Fade.X) / (label.Fade.Y - label.Fade.X), "
        + "((placed.Scale.Y * camera.Scale.Y * carried.Scale.Y) - label.Fade.Z) / (label.Fade.W - label.Fade.Z)), 0, 1)"
        + " * label.Arrival * carried.Opacity";

    private readonly Compositor _compositor;

    private readonly MapCamera _camera;

    private readonly IMotionPolicy _motion;

    /// <summary>
    /// Where the drawing the names were laid out for lies in the picture, per device-independent
    /// pixel of that drawing: a <c>Scale</c> and an <c>Offset</c>, combined with the camera's by the
    /// compositor.
    /// </summary>
    private readonly CompositionPropertySet _placed;

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

    private CompositionEasingFunction? _ease;

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

        _placed = _compositor.CreatePropertySet();
        _placed.InsertVector3(nameof(Visual.Scale), Vector3.One);
        _placed.InsertVector3(nameof(Visual.Offset), Vector3.Zero);
    }

    /// <summary>
    /// Put the names where <paramref name="placed"/> says one device-independent pixel of the drawing
    /// they name lies in the picture.
    /// </summary>
    public void Place(MapTransform placed)
    {
        _placed.InsertVector3(nameof(Visual.Scale), new Vector3((float)placed.ScaleX, (float)placed.ScaleY, 1));
        _placed.InsertVector3(nameof(Visual.Offset), new Vector3((float)placed.X, (float)placed.Y, 0));
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
    /// a part of Windows.</para>
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
    public void Show(ExploreSurface drawing, double scale, Func<ExploreLabel, string> caption)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(caption);

        Visibility = Visibility.Visible;

        while (Children.Count < drawing.Labels.Count)
        {
            var name = new Label(_compositor, _camera.Properties, _placed, _carried);

            _names.Add(name);
            Children.Add(name.Text);
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
            var text = name.Text;

            text.Text = caption(label);
            text.MaxWidth = label.Width / scale;

            ((SolidColorBrush)text.Foreground).Color = Color.FromArgb(
                255, label.Colour.Red, label.Colour.Green, label.Colour.Blue);

            ((RotateTransform)text.RenderTransform).Angle = label.Rotation;

            // Measured here rather than left to the layout pass, because where the text goes and when
            // it fades both depend on how much of the box it fills. The pass that follows asks the same
            // question and is answered from this.
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var measured = text.DesiredSize;
            var (across, down) = label.Fade((float)(measured.Width * scale), (float)(measured.Height * scale));
            var (anchorX, anchorY) = label.Anchor;

            name.Properties.InsertVector2("Anchor", new Vector2((float)(anchorX / scale), (float)(anchorY / scale)));
            name.Properties.InsertScalar("Shift", label.Centred ? (float)(measured.Width / 2) : 0);
            name.Properties.InsertVector4("Fade", new Vector4(across.Gone, across.Whole, down.Gone, down.Whole));

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
                name.Enter(arrival, _ease ??= _compositor.CreateCubicBezierEasingFunction(
                    Motion.EaseControlPoints.First,
                    Motion.EaseControlPoints.Second));
            }

            name.Node = label.Node;
            _naming.Add(label.Node);
        }

        (_named, _naming) = (_naming, _named);
        _tree = drawing.Tree;
    }

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
        Visibility = Visibility.Visible;
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

        public Label(Compositor compositor, CompositionPropertySet camera, CompositionPropertySet placed, Visual carried)
        {
            Text = new TextBlock
            {
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(),

                // Zero for anything in rectangles, and per label for a sunburst, which turns each one
                // to lie along its own ring. Always present rather than attached only where it turns:
                // a transform of no degrees costs nothing to keep, and a branch here would mean a
                // label reused from a sunburst kept its angle on a treemap.
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(),
            };

            // Announced through the list view instead: a label here duplicates a row there, and a
            // screen reader reading fifty fragments of a picture helps nobody.
            AutomationProperties.SetAccessibilityView(Text, AccessibilityView.Raw);

            Properties = compositor.CreatePropertySet();
            Properties.InsertVector2("Anchor", Vector2.Zero);
            Properties.InsertScalar("Shift", 0);
            Properties.InsertVector4("Fade", new Vector4(0, 1, 0, 1));
            Properties.InsertScalar("Arrival", 1);

            _placement = compositor.CreateExpressionAnimation(PlacementExpression);
            _opacity = compositor.CreateExpressionAnimation(OpacityExpression);

            foreach (var expression in new[] { _placement, _opacity })
            {
                expression.SetReferenceParameter("label", Properties);
                expression.SetReferenceParameter("camera", camera);
                expression.SetReferenceParameter("placed", placed);
            }

            _visual = ElementCompositionPreview.GetElementVisual(Text);
            ElementCompositionPreview.SetIsTranslationEnabled(Text, true);

            Follow(carried);
        }

        public TextBlock Text { get; }

        /// <summary>
        /// Where the name is and how it fades, in its own coordinates: <c>Anchor</c>, <c>Shift</c>,
        /// <c>Fade</c> and <c>Arrival</c>.
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

            _visual.StartAnimation("Translation", _placement);
            _visual.StartAnimation(nameof(Visual.Opacity), _opacity);
        }

        /// <summary>Be there at once.</summary>
        public void Arrive()
        {
            Properties.StopAnimation("Arrival");
            Properties.InsertScalar("Arrival", 1);
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

            Properties.StartAnimation("Arrival", fade);
        }
    }
}
