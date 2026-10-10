using Deguffer.App.Shell;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Deguffer.App.Controls;

/// <summary>
/// How a bar moves to its figure: each new figure is eased to from wherever the bar is drawn, for as
/// long as <see cref="MotionToken.Bar"/> says, and <see cref="Grow"/> brings it in from nothing. The
/// one motion for a bar along a row (<see cref="FillBar"/>) and a column of a chart
/// (<see cref="ColumnBar"/>), which differ only in which way the fill is shifted.
///
/// <para>On the compositor. The fill is laid out the whole length of the bar and drawn shifted back by
/// the part the figure does not reach, so a move is a translation the compositor clocks: nothing is
/// laid out again on any frame of it, and the fill keeps its rounded end at every length. The bar
/// clips it to its own shape.</para>
///
/// <para>A bar shown for a different row, as a list hands its containers from row to row while it
/// scrolls, is drawn at that row's figure at once. Easing there would play a change nobody made.</para>
/// </summary>
internal sealed class BarMotion
{
    /// <summary>"Fraction", how much of the bar the fill covers, and "Length", the bar's own.</summary>
    private readonly CompositionPropertySet _drawn;

    private readonly CompositionEasingFunction _ease;

    /// <summary>Whether the bar has drawn a figure yet. Its first is drawn where it stands.</summary>
    private bool _hasDrawn;

    /// <summary>The row the drawn figure belongs to, so a figure for another row is not eased to.</summary>
    private object? _drawnFor;

    /// <param name="fill">What is shifted to show the figure.</param>
    /// <param name="shift">
    /// The fill's translation as an expression over <c>drawn.Fraction</c> and <c>drawn.Length</c>.
    /// Bound to the length as well as to the figure, so a bar resized during a move carries the fill
    /// with it rather than leaving it where the old length put it.
    /// </param>
    public BarMotion(UIElement fill, string shift)
    {
        var visual = ElementCompositionPreview.GetElementVisual(fill);
        var compositor = visual.Compositor;

        _drawn = compositor.CreatePropertySet();
        _drawn.InsertScalar("Fraction", 0);
        _drawn.InsertScalar("Length", 0);

        _ease = compositor.CreateCubicBezierEasingFunction(Motion.EaseControlPoints.First, Motion.EaseControlPoints.Second);

        var translation = compositor.CreateExpressionAnimation(shift);
        translation.SetReferenceParameter("drawn", _drawn);
        ElementCompositionPreview.SetIsTranslationEnabled(fill, true);
        visual.StartAnimation("Translation", translation);
    }

    /// <summary>The bar is <paramref name="length"/> long now, along the way its fill moves.</summary>
    public void Measure(float length) => _drawn.InsertScalar("Length", length);

    /// <summary>
    /// Bring the bar in from nothing to <paramref name="fraction"/>, for <paramref name="row"/>.
    /// Nothing happens with motion off: the bar is at its figure already.
    /// </summary>
    public void Grow(float fraction, object? row)
    {
        var played = SystemMotion.Current.For(MotionToken.Bar);

        if (played.IsInstant)
        {
            return;
        }

        _drawn.StopAnimation("Fraction");
        _drawn.InsertScalar("Fraction", 0);
        Ease(fraction, played);
        Remember(row);
    }

    /// <summary>
    /// Show <paramref name="fraction"/> for <paramref name="row"/>: eased to from where the bar is
    /// for the row it already shows, and at once for a first figure or another row.
    /// </summary>
    public void Follow(float fraction, object? row)
    {
        var played = SystemMotion.Current.For(MotionToken.Bar);

        if (!_hasDrawn || !ReferenceEquals(row, _drawnFor) || played.IsInstant)
        {
            _drawn.StopAnimation("Fraction");
            _drawn.InsertScalar("Fraction", fraction);
        }
        else
        {
            Ease(fraction, played);
        }

        Remember(row);
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

    private void Remember(object? row)
    {
        _hasDrawn = true;
        _drawnFor = row;
    }
}
