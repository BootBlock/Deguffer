using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Deguffer.App.Controls;

/// <summary>
/// A bar along a row that follows its figure, moved as <see cref="BarMotion"/> moves every bar. The
/// track clips the fill to the bar's own rounded shape.
/// </summary>
public sealed partial class FillBar
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(FillBar),
        new PropertyMetadata(0.0, (bar, _) => ((FillBar)bar).Follow()));

    private readonly BarMotion _motion;

    private readonly CompositionRoundedRectangleGeometry _outline;

    public FillBar()
    {
        InitializeComponent();

        _motion = new BarMotion(Fill, "Vector3((drawn.Fraction - 1) * drawn.Length, 0, 0)");

        var compositor = ElementCompositionPreview.GetElementVisual(Track).Compositor;

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
    public void Grow() => _motion.Grow(Fraction, DataContext);

    private float Fraction => (float)Math.Clamp(Value / 100, 0, 1);

    private void Follow() => _motion.Follow(Fraction, DataContext);

    private void Shape(Windows.Foundation.Size size)
    {
        var width = (float)size.Width;
        var height = (float)size.Height;

        _motion.Measure(width);
        _outline.Size = new Vector2(width, height);
        _outline.CornerRadius = new Vector2(height / 2);
        Fill.CornerRadius = new CornerRadius(height / 2);
    }
}
