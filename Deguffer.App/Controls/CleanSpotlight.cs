using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Execution;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The light a clean puts on the row it is working on, drawn over the row's own container.
///
/// <para>Four layers, clipped to the row. A wash of the accent fills the row from the left as far as
/// the run has got through it, strongest at its leading edge, which is a bright line with a glow round
/// it, gliding at an even speed between the run's reports. A slanted band of light travels the whole
/// length of the row and back, like a marquee, slowing into each end and turning there. A frame round
/// the row and a rail down its left breathe in time with it. A row that is only
/// being checked (§5.6) has no wash or edge, because nothing on it is being removed.</para>
///
/// <para>Composition visuals with no drawing surface, played by the compositor: nothing runs per frame
/// on the UI thread, and every layer is sized by an expression on one property set, so a row that
/// changes size moves one value. One set of visuals for the page, moved from row to row, because only
/// one row is ever being cleaned.</para>
///
/// <para>Decoration: the status line says in words what the run is doing, and assistive technology never
/// sees this. With motion off nothing sweeps or breathes (<see cref="MotionToken.Spotlight"/>). In high
/// contrast only the frame and the rail are drawn (<see cref="CleanSpotlightLook"/>).</para>
/// </summary>
internal sealed class CleanSpotlight
{
    /// <summary>The width of the edge's glow. Its bright line is the middle few pixels.</summary>
    private const float EdgeWidth = 40;

    /// <summary>The narrowest the sweeping band is, and its share of a wider row.</summary>
    private const float SweepMinimum = 160;
    private const float SweepShare = 0.3f;

    /// <summary>How far the band leans, as the tangent of its slant: its top leads.</summary>
    private const float Lean = 0.42f;

    private const float FrameThickness = 1.5f;
    private const float FrameCorner = 6;
    private const float RailWidth = 3;
    private const float RailInset = 8;

    /// <summary>Faint where the row starts and strongest where the run has got to, so the wash leans forward.</summary>
    private static readonly (float Offset, float Alpha)[] WashStops = [(0, 0.05f), (0.8f, 0.16f), (1, 0.3f)];

    private static readonly (float Offset, float Alpha)[] EdgeStops = [(0, 0), (0.42f, 0.22f), (0.5f, 0.95f), (0.58f, 0.22f), (1, 0)];

    private static readonly (float Offset, float Alpha)[] SweepStops = [(0, 0), (0.5f, 0.16f), (1, 0)];

    private readonly Compositor _compositor;
    private readonly IMotionPolicy _motion;
    /// <summary>Every layer, clipped to the row: one set for the page, moved into each row's own root.</summary>
    private readonly ContainerVisual _layers;

    /// <summary>Size, the row's own size. Fill, how far through the row the run is. Sweep, the band's place.</summary>
    private readonly CompositionPropertySet _state;

    private readonly SpriteVisual _wash;
    private readonly SpriteVisual _edge;
    private readonly SpriteVisual _sweep;
    private readonly ShapeVisual _marks;

    private readonly CompositionColorGradientStop[] _washStops;
    private readonly CompositionColorGradientStop[] _edgeStops;
    private readonly CompositionColorGradientStop[] _sweepStops;
    private readonly CompositionColorBrush _markBrush;

    /// <summary>
    /// An even pace for the wash following its figure. Reports arrive closer together than a move
    /// lasts, so each move starts from wherever the last one had got to: an ease would slow to a stop
    /// at every report and the edge would step rather than glide.
    /// </summary>
    private readonly LinearEasingFunction _even;

    /// <summary>
    /// The animations, each built once and started again as often as it is needed: an animation is
    /// copied when it starts, so changing one afterwards moves nothing already playing. The wash's move
    /// has its key frame and duration written before each start.
    /// </summary>
    private readonly ScalarKeyFrameAnimation _fillMove;
    private readonly ScalarKeyFrameAnimation _fadeIn;
    private readonly ScalarKeyFrameAnimation _sweepLoop;
    private readonly ScalarKeyFrameAnimation _breathing;

    /// <summary>
    /// The roots handed to the rows in turn, each holding <see cref="_layers"/> while its row is lit. A
    /// visual handed straight from one row to the next stayed drawn on the first, with no error raised,
    /// so the next row always gets the other root, and the layers move between them through their
    /// parent's children.
    /// </summary>
    private readonly ContainerVisual[] _roots;

    private int _nextRoot;

    /// <summary>The row the light is on and the one of <see cref="_roots"/> handed to it, or null while it is on none.</summary>
    private (FrameworkElement Host, ContainerVisual Root)? _lit;

    private PlanStage _stage;
    private CleanSpotlightLook _look;

    /// <summary>The look the gradient stops are tinted in, so a row in the same look writes no colour.</summary>
    private CleanSpotlightLook? _tinted;

    /// <summary>Whether the sweep and the breathing are playing, so a new row carries them on rather than restarting them.</summary>
    private bool _looping;

    public CleanSpotlight(Compositor compositor, IMotionPolicy motion)
    {
        _compositor = compositor;
        _motion = motion;

        _even = compositor.CreateLinearEasingFunction();
        var swing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.55f, 1));

        _fillMove = compositor.CreateScalarKeyFrameAnimation();
        _fadeIn = compositor.CreateScalarKeyFrameAnimation();
        _fadeIn.InsertKeyFrame(0, 0);
        _fadeIn.InsertKeyFrame(1, 1);
        _sweepLoop = BackAndForth(0, 1, swing);
        _breathing = BackAndForth(1, 0.45f, swing);

        _state = compositor.CreatePropertySet();
        _state.InsertVector2("Size", Vector2.Zero);
        _state.InsertScalar("Fill", 0);
        _state.InsertScalar("Sweep", 0);

        _roots = [compositor.CreateContainerVisual(), compositor.CreateContainerVisual()];
        _layers = compositor.CreateContainerVisual();
        Bind(_layers, nameof(Visual.Size), "p.Size");
        _layers.Clip = compositor.CreateInsetClip();

        (_wash, _washStops) = Gradient(WashStops);
        Bind(_wash, nameof(Visual.Size), "Vector2(p.Size.X * p.Fill, p.Size.Y)");

        (_edge, _edgeStops) = Gradient(EdgeStops);
        Bind(_edge, nameof(Visual.Size), "Vector2(EdgeWidth, p.Size.Y)");
        Bind(_edge, nameof(Visual.Offset), "Vector3(p.Size.X * p.Fill - EdgeWidth / 2, 0, 0)");

        (_sweep, _sweepStops) = Gradient(SweepStops);
        _sweep.TransformMatrix = new Matrix4x4(Matrix3x2.CreateSkew(-MathF.Atan(Lean), 0));
        const string SweepWidth = "Max(SweepMinimum, p.Size.X * SweepShare)";
        Bind(_sweep, nameof(Visual.Size), $"Vector2({SweepWidth}, p.Size.Y)");

        // From the row's left end to its right, slant and all, so the band turns inside the row rather
        // than leaving it: its foot leans back by the row's height times the lean.
        Bind(_sweep, nameof(Visual.Offset), $"Vector3(p.Size.Y * Lean + (p.Size.X - {SweepWidth} - p.Size.Y * Lean) * p.Sweep, 0, 0)");

        _markBrush = compositor.CreateColorBrush();
        _marks = Marks();

        _layers.Children.InsertAtTop(_wash);
        _layers.Children.InsertAtTop(_sweep);
        _layers.Children.InsertAtTop(_edge);
        _layers.Children.InsertAtTop(_marks);
    }

    /// <summary>The row the light is on, or null while it is on none.</summary>
    public FrameworkElement? Host => _lit?.Host;

    /// <summary>
    /// Put the light on <paramref name="host"/>, <paramref name="fraction"/> of the way through it, taking
    /// it off any row it was on. It fades in on a row it was not already on.
    /// </summary>
    public void Show(FrameworkElement host, PlanStage stage, double fraction, CleanSpotlightLook look)
    {
        if (!ReferenceEquals(host, Host))
        {
            Detach();

            var root = _roots[_nextRoot];
            _nextRoot ^= 1;
            _lit = (host, root);

            host.SizeChanged += OnHostSizeChanged;
            _state.InsertVector2("Size", host.ActualSize);
            root.Children.InsertAtTop(_layers);
            ElementCompositionPreview.SetElementChildVisual(host, root);
            _fadeIn.Duration = _motion.For(MotionToken.Crossfade).Duration;
            root.StartAnimation(nameof(Visual.Opacity), _fadeIn);
        }

        _stage = stage;
        _state.StopAnimation("Fill");
        _state.InsertScalar("Fill", (float)fraction);
        Restyle(look);
    }

    /// <summary>The run has got <paramref name="fraction"/> of the way through the row.</summary>
    public void Fill(double fraction)
    {
        var motion = _motion.For(MotionToken.Bar);

        if (motion.IsInstant)
        {
            _state.InsertScalar("Fill", (float)fraction);
            return;
        }

        // From where the wash is drawn to the figure just reported, as a bar follows its figure. A key
        // frame inserted where one already is replaces it.
        _fillMove.InsertKeyFrame(1, (float)fraction, _even);
        _fillMove.Duration = motion.Duration;
        _state.StartAnimation("Fill", _fillMove);
    }

    /// <summary>
    /// Draw in <paramref name="look"/>, and sweep and breathe only if the reader's animation setting
    /// allows it. For a theme, a high contrast theme or that setting changing while the light is on.
    /// </summary>
    public void Restyle(CleanSpotlightLook look)
    {
        _look = look;

        if (_tinted != look)
        {
            Tint(_washStops, WashStops, look.Mark);
            Tint(_edgeStops, EdgeStops, look.Mark);
            Tint(_sweepStops, SweepStops, look.Shine);
            _markBrush.Color = look.Mark;
            _tinted = look;
        }

        var removing = _stage == PlanStage.Removing && !look.Contrast;
        _wash.IsVisible = removing;
        _edge.IsVisible = removing;

        var moving = _motion.For(MotionToken.Spotlight).Travels && !look.Contrast;
        _sweep.IsVisible = moving;

        if (!moving)
        {
            Still();
        }
        else if (!_looping)
        {
            _state.StartAnimation("Sweep", _sweepLoop);
            _marks.StartAnimation(nameof(Visual.Opacity), _breathing);
            _looping = true;
        }
    }

    /// <summary>Restyle in the look it has, for the reader's animation setting changing.</summary>
    public void Restyle() => Restyle(_look);

    /// <summary>Take the light off the row it is on, at once, and stop its loops.</summary>
    public void Hide()
    {
        Detach();
        Still();
    }

    private void Still()
    {
        _state.StopAnimation("Sweep");
        _marks.StopAnimation(nameof(Visual.Opacity));
        _marks.Opacity = 1;
        _looping = false;
    }

    private void Detach()
    {
        if (_lit is not (var host, var root))
        {
            return;
        }

        host.SizeChanged -= OnHostSizeChanged;
        root.Children.RemoveAll();
        ElementCompositionPreview.SetElementChildVisual(host, null);
        _lit = null;
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        _state.InsertVector2("Size", new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));

    /// <summary>The frame round the row and the rail down its left, both in the mark's colour.</summary>
    private ShapeVisual Marks()
    {
        var marks = _compositor.CreateShapeVisual();
        Bind(marks, nameof(Visual.Size), "p.Size");

        // Inset by half the stroke, so the whole line falls inside the row's clip.
        var frame = _compositor.CreateRoundedRectangleGeometry();
        frame.CornerRadius = new Vector2(FrameCorner);
        frame.Offset = new Vector2(FrameThickness / 2);
        Bind(frame, nameof(CompositionRoundedRectangleGeometry.Size), "Vector2(Max(0, p.Size.X - FrameThickness), Max(0, p.Size.Y - FrameThickness))");

        var outline = _compositor.CreateSpriteShape(frame);
        outline.StrokeBrush = _markBrush;
        outline.StrokeThickness = FrameThickness;

        var rail = _compositor.CreateRoundedRectangleGeometry();
        rail.CornerRadius = new Vector2(RailWidth / 2);
        rail.Offset = new Vector2(RailWidth + 2, RailInset);
        Bind(rail, nameof(CompositionRoundedRectangleGeometry.Size), "Vector2(RailWidth, Max(0, p.Size.Y - 2 * RailInset))");

        var bar = _compositor.CreateSpriteShape(rail);
        bar.FillBrush = _markBrush;

        marks.Shapes.Add(outline);
        marks.Shapes.Add(bar);

        return marks;
    }

    /// <summary>A layer filled left to right with a gradient, whose stops <see cref="Tint"/> colours.</summary>
    private (SpriteVisual Visual, CompositionColorGradientStop[] Stops) Gradient((float Offset, float Alpha)[] stops)
    {
        var brush = _compositor.CreateLinearGradientBrush();
        brush.StartPoint = new Vector2(0, 0.5f);
        brush.EndPoint = new Vector2(1, 0.5f);

        var made = new CompositionColorGradientStop[stops.Length];
        for (var i = 0; i < stops.Length; i++)
        {
            made[i] = _compositor.CreateColorGradientStop(stops[i].Offset, default);
            brush.ColorStops.Add(made[i]);
        }

        var visual = _compositor.CreateSpriteVisual();
        visual.Brush = brush;

        return (visual, made);
    }

    /// <summary>Colour each stop <paramref name="colour"/>, at the alpha its layer gives it.</summary>
    private static void Tint(CompositionColorGradientStop[] stops, (float Offset, float Alpha)[] layer, Color colour)
    {
        for (var i = 0; i < stops.Length; i++)
        {
            stops[i].Color = Color.FromArgb((byte)(layer[i].Alpha * 255), colour.R, colour.G, colour.B);
        }
    }

    /// <summary>
    /// Drive <paramref name="property"/> by <paramref name="expression"/> over the state. The constants
    /// are parameters rather than text in the expression, because a number written into it takes the
    /// reader's culture, and a decimal comma is not an expression.
    /// </summary>
    private void Bind(CompositionObject target, string property, string expression)
    {
        var bound = _compositor.CreateExpressionAnimation(expression);
        bound.SetReferenceParameter("p", _state);
        bound.SetScalarParameter(nameof(EdgeWidth), EdgeWidth);
        bound.SetScalarParameter(nameof(SweepMinimum), SweepMinimum);
        bound.SetScalarParameter(nameof(SweepShare), SweepShare);
        bound.SetScalarParameter(nameof(Lean), Lean);
        bound.SetScalarParameter(nameof(FrameThickness), FrameThickness);
        bound.SetScalarParameter(nameof(RailWidth), RailWidth);
        bound.SetScalarParameter(nameof(RailInset), RailInset);
        target.StartAnimation(property, bound);
    }

    /// <summary>
    /// From <paramref name="from"/> to <paramref name="to"/> and back again for as long as it runs, each
    /// way one <see cref="MotionToken.Spotlight"/> long. Played backwards on the way back rather than as a
    /// second half of the loop, so the turn at each end is the same eased stop and start.
    /// </summary>
    /// <param name="swing">An ease in and out, so the band slows into each end and the breathing has no corner.</param>
    private ScalarKeyFrameAnimation BackAndForth(float from, float to, CompositionEasingFunction swing)
    {
        var loop = _compositor.CreateScalarKeyFrameAnimation();
        loop.InsertKeyFrame(0, from);
        loop.InsertKeyFrame(1, to, swing);
        loop.Duration = MotionToken.Spotlight.Full.Duration;
        loop.Direction = AnimationDirection.Alternate;
        loop.IterationBehavior = AnimationIterationBehavior.Forever;

        return loop;
    }
}
