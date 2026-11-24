using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Deguffer.App.Controls;

/// <summary>
/// Its children in equal columns side by side, or stacked one above another where a column would be
/// narrower than <see cref="MinimumColumnWidth"/>. <see cref="Columns"/> decides which.
///
/// <para>For a region of two halves that each hold controls, such as where Duplicates searches and
/// what makes a match: in a Grid's two star columns on a narrow window, each half runs its controls
/// off its own edge, half drawn, while the other half has the same shortage.</para>
/// </summary>
public sealed partial class SplitColumns : Panel
{
    public static readonly DependencyProperty MinimumColumnWidthProperty = DependencyProperty.Register(
        nameof(MinimumColumnWidth), typeof(double), typeof(SplitColumns), new PropertyMetadata(0.0, OnLayoutChanged));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(SplitColumns), new PropertyMetadata(0.0, OnLayoutChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(SplitColumns), new PropertyMetadata(0.0, OnLayoutChanged));

    private bool _sideBySide;

    /// <summary>The narrowest a column may be before the columns stack.</summary>
    public double MinimumColumnWidth
    {
        get => (double)GetValue(MinimumColumnWidthProperty);
        set => SetValue(MinimumColumnWidthProperty, value);
    }

    /// <summary>The gap between columns side by side.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>The gap between parts stacked.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count;
        _sideBySide = Columns.SideBySide(count, MinimumColumnWidth, ColumnSpacing, availableSize.Width);

        if (_sideBySide)
        {
            var width = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Columns.Width(count, ColumnSpacing, availableSize.Width);
            var height = 0.0;
            var used = 0.0;

            for (var i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                child.Measure(new Size(width, availableSize.Height));
                height = Math.Max(height, child.DesiredSize.Height);
                used += child.DesiredSize.Width;
            }

            return new Size(
                double.IsInfinity(width) ? used + (Math.Max(0, count - 1) * ColumnSpacing) : availableSize.Width,
                height);
        }

        var stacked = 0.0;
        var widest = 0.0;

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            stacked += child.DesiredSize.Height;
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        return new Size(widest, stacked + (Math.Max(0, count - 1) * RowSpacing));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_sideBySide)
        {
            var width = Columns.Width(Children.Count, ColumnSpacing, finalSize.Width);
            var left = 0.0;

            for (var i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                child.Arrange(new Rect(left, 0, width, finalSize.Height));
                left += width + ColumnSpacing;
            }

            return finalSize;
        }

        var top = 0.0;

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
            top += child.DesiredSize.Height + RowSpacing;
        }

        return finalSize;
    }

    private static void OnLayoutChanged(DependencyObject panel, DependencyPropertyChangedEventArgs args) =>
        ((SplitColumns)panel).InvalidateMeasure();
}
