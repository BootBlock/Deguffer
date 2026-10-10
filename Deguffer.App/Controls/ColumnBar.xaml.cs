using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Deguffer.App.Controls;

/// <summary>
/// A column of a chart that follows its figure, moved as <see cref="BarMotion"/> moves every bar: the
/// fill stands the column's whole height and is drawn shifted down by the part the figure does not
/// reach, under a clip at the column's edges, so it rises from the baseline with its rounded top.
/// </summary>
public sealed partial class ColumnBar
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(ColumnBar),
        new PropertyMetadata(0.0, (bar, _) => ((ColumnBar)bar).Follow()));

    private readonly BarMotion _motion;

    public ColumnBar()
    {
        InitializeComponent();

        _motion = new BarMotion(Fill, "Vector3(0, (1 - drawn.Fraction) * drawn.Length, 0)");

        var column = ElementCompositionPreview.GetElementVisual(Column);
        column.Clip = column.Compositor.CreateInsetClip();

        Column.SizeChanged += (_, e) => _motion.Measure((float)e.NewSize.Height);
    }

    /// <summary>The figure, out of 100.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Bring the column up from the baseline to its figure. Nothing happens with motion off.</summary>
    public void Grow() => _motion.Grow(Fraction, DataContext);

    private float Fraction => (float)Math.Clamp(Value / 100, 0, 1);

    private void Follow() => _motion.Follow(Fraction, DataContext);
}
