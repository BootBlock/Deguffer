using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Deguffer.App.Controls;

/// <summary>
/// A bar that follows its figure: each new <see cref="Value"/> is eased to from wherever the bar is
/// drawn, for as long as <see cref="MotionToken.Bar"/> says, and <see cref="Grow"/> brings it in from
/// nothing.
///
/// <para>On the compositor. The fill is laid out the whole width of the track and drawn shifted left by
/// the part the figure does not reach, so a move is a translation the compositor clocks: nothing is
/// laid out again on any frame of it, and the fill keeps its rounded end at every length. The track
/// clips it to the bar's own rounded shape.</para>
///
/// <para>A bar shown for a different row, as a list hands its containers from row to row while it
/// scrolls, is drawn at that row's figure at once. Easing there would play a change nobody made.</para>
/// </summary>
public sealed partial class FillBar
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(FillBar),
        new PropertyMetadata(0.0, (bar, _) => ((FillBar)bar).Follow()));

    /// <summary>"Fraction", how much of the track the fill covers, and "Width", the track's own.</summary>
    private readonly CompositionPropertySet _drawn;

    private readonly CompositionRoundedRectangleGeometry _outline;

    private readonly CompositionEasingFunction _ease;

    /// <summary>Whether the bar has drawn a figure yet. Its first is drawn where it stands.</summary>
    private bool _hasDrawn;

    /// <summary>The row the drawn figure belongs to, so a figure for another row is not eased to.</summary>
    private object? _drawnFor;

    public FillBar()
    {
        InitializeComponent();

        var fill = ElementCompositionPreview.GetElementVisual(Fill);
        var compositor = fill.Compositor;

        _drawn = compositor.CreatePropertySet();
        _drawn.InsertScalar("Fraction", 0);
        _drawn.InsertScalar("Width", 0);

        _ease = compositor.CreateCubicBezierEasingFunction(Motion.EaseControlPoints.First, Motion.EaseControlPoints.Second);

        // Bound to the track's width as well as to the figure, so a window resized during a move
        // carries the fill with it rather than leaving it where the old width put it.
        var shift = compositor.CreateExpressionAnimation("Vector3((drawn.Fraction - 1) * drawn.Width, 0, 0)");
        shift.SetReferenceParameter("drawn", _drawn);
        ElementCompositionPreview.SetIsTranslationEnabled(Fill, true);
        fill.StartAnimation("Translation", shift);

        _outline = compositor.CreateRoundedRectangleGeometry();
        ElementCompositionPreview.GetElementVisual(Track).Clip = compositor.CreateGeometricClip(_outline);

        Track.SizeChanged += (_, e) => Shape(e.NewSize);
    }

    /// <summary>The figure, out of 100.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>
    /// Bring the bar in from nothing to its figure, as its row arrives. Nothing happens with motion
    /// off: the bar is at its figure already.
    /// </summary>
    public void Grow()
    {
        var played = SystemMotion.Current.For(MotionToken.Bar);

        if (played.IsInstant)
        {
            return;
        }

        _drawn.StopAnimation("Fraction");
        _drawn.InsertScalar("Fraction", 0);
        Ease(Fraction, played);
        Remember();
    }

    private float Fraction => (float)Math.Clamp(Value / 100, 0, 1);

    private void Follow()
    {
        var played = SystemMotion.Current.For(MotionToken.Bar);

        if (!_hasDrawn || !ReferenceEquals(DataContext, _drawnFor) || played.IsInstant)
        {
            _drawn.StopAnimation("Fraction");
            _drawn.InsertScalar("Fraction", Fraction);
        }
        else
        {
            Ease(Fraction, played);
        }

        Remember();
    }

    /// <summary>
    /// Move from where the bar is drawn now, part of the way through an earlier move or not, so a
    /// figure reported during a move carries on from there rather than jumping back.
    /// </summary>
    private void Ease(float to, Motion played)
    {
        var move = _drawn.Compositor.CreateScalarKeyFrameAnimation();
        move.InsertExpressionKeyFrame(0, "this.StartingValue");
        move.InsertKeyFrame(1, to, _ease);
        move.Duration = played.Duration;
        _drawn.StartAnimation("Fraction", move);
    }

    private void Remember()
    {
        _hasDrawn = true;
        _drawnFor = DataContext;
    }

    private void Shape(Windows.Foundation.Size size)
    {
        var width = (float)size.Width;
        var height = (float)size.Height;

        _drawn.InsertScalar("Width", width);
        _outline.Size = new Vector2(width, height);
        _outline.CornerRadius = new Vector2(height / 2);
        Fill.CornerRadius = new CornerRadius(height / 2);
    }
}
