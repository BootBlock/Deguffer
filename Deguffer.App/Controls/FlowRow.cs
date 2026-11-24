using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Deguffer.App.Controls;

/// <summary>
/// A horizontal row of controls that wraps onto a further line, left-aligned, where the next control
/// would cross the right edge. <see cref="Flow"/> decides where a line breaks.
///
/// <para>For a row whose controls are peers, such as the check boxes saying what makes a duplicate
/// or a toolbar: a horizontal <c>StackPanel</c> lets the last of them run off the edge of a narrow
/// window, half drawn, which reads as a defect rather than as a shortage of room. Each control keeps
/// its own width, and each is given the height of its line's tallest and placed in it by its own
/// <c>VerticalAlignment</c>, as in a Grid cell.</para>
/// </summary>
public sealed partial class FlowRow : Panel
{
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(FlowRow), new PropertyMetadata(0.0, OnSpacingChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(FlowRow), new PropertyMetadata(0.0, OnSpacingChanged));

    /// <summary>The gap between controls on one line.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>The gap between lines.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Each control is offered a whole line, the most it can have on any line: one wider than
        // that, such as a long breadcrumb step, trims to the line instead of running off the edge.
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(availableSize);
        }

        return Lay(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Lay(finalSize.Width, arrange: true);

        return finalSize;
    }

    /// <summary>
    /// Walk the children line by line, arranging each where <paramref name="arrange"/> says to, and
    /// return the size the lines take. One walk serves both passes, so the two cannot disagree on
    /// where a line breaks.
    /// </summary>
    private Size Lay(double available, bool arrange)
    {
        var gap = ColumnSpacing;
        var widest = 0.0;
        var top = 0.0;
        var start = 0;

        while (start < Children.Count)
        {
            // Find where this line ends and how tall it is, then place it.
            var line = 0.0;
            var height = 0.0;
            var end = start;

            for (; end < Children.Count; end++)
            {
                var size = Children[end].DesiredSize;

                if (Flow.Breaks(line, size.Width, gap, available))
                {
                    break;
                }

                line += (line > 0 && size.Width > 0 ? gap : 0) + size.Width;
                height = Math.Max(height, size.Height);
            }

            if (arrange)
            {
                var left = 0.0;

                for (var i = start; i < end; i++)
                {
                    var size = Children[i].DesiredSize;
                    left += left > 0 && size.Width > 0 ? gap : 0;
                    Children[i].Arrange(new Rect(left, top, size.Width, height));
                    left += size.Width;
                }
            }

            widest = Math.Max(widest, line);
            top += height + (end < Children.Count ? RowSpacing : 0);
            start = end;
        }

        return new Size(double.IsInfinity(available) ? widest : Math.Min(widest, available), top);
    }

    private static void OnSpacingChanged(DependencyObject row, DependencyPropertyChangedEventArgs args) =>
        ((FlowRow)row).InvalidateMeasure();
}
