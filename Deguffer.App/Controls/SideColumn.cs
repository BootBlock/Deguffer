using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Deguffer.App.Controls;

/// <summary>
/// A picture with a card beside it at its right. The first child is the picture and takes what the
/// card leaves; every later child is a card for that place, of which the page shows at most one at a
/// time. <see cref="SideShare"/> sets the card's width.
///
/// <para>A card takes a share of the width, so the picture keeps most of a narrow window, and the
/// picture takes everything while no card is visible, where a star column would hold the card's
/// room empty. Where the card would leave the
/// picture narrower than <see cref="MinimumMainWidth"/>, the card goes under the picture instead,
/// taking the same share of the height.</para>
/// </summary>
public sealed partial class SideColumn : Panel
{
    public static readonly DependencyProperty ShareProperty = DependencyProperty.Register(
        nameof(Share), typeof(double), typeof(SideColumn), new PropertyMetadata(0.4, OnLayoutChanged));

    public static readonly DependencyProperty MinimumSideWidthProperty = DependencyProperty.Register(
        nameof(MinimumSideWidth), typeof(double), typeof(SideColumn), new PropertyMetadata(0.0, OnLayoutChanged));

    public static readonly DependencyProperty MaximumSideWidthProperty = DependencyProperty.Register(
        nameof(MaximumSideWidth), typeof(double), typeof(SideColumn), new PropertyMetadata(double.PositiveInfinity, OnLayoutChanged));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(SideColumn), new PropertyMetadata(0.0, OnLayoutChanged));

    public static readonly DependencyProperty MinimumMainWidthProperty = DependencyProperty.Register(
        nameof(MinimumMainWidth), typeof(double), typeof(SideColumn), new PropertyMetadata(0.0, OnLayoutChanged));

    /// <summary>The part of the width a card takes, before its limits.</summary>
    public double Share
    {
        get => (double)GetValue(ShareProperty);
        set => SetValue(ShareProperty, value);
    }

    /// <summary>The narrowest a card may be.</summary>
    public double MinimumSideWidth
    {
        get => (double)GetValue(MinimumSideWidthProperty);
        set => SetValue(MinimumSideWidthProperty, value);
    }

    /// <summary>The widest a card may be.</summary>
    public double MaximumSideWidth
    {
        get => (double)GetValue(MaximumSideWidthProperty);
        set => SetValue(MaximumSideWidthProperty, value);
    }

    /// <summary>The gap between the picture and the card.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>The narrowest the picture may be beside a card before the card goes under it.</summary>
    public double MinimumMainWidth
    {
        get => (double)GetValue(MinimumMainWidthProperty);
        set => SetValue(MinimumMainWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;

        for (var i = 0; i < Children.Count; i++)
        {
            var slot = Place(i, availableSize);
            Children[i].Measure(new Size(slot.Width, slot.Height));
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(Place(i, finalSize));
        }

        return finalSize;
    }

    /// <summary>
    /// Where child <paramref name="index"/> goes in a region of <paramref name="size"/>: the picture
    /// first, beside the card or above it, and each card in the card's place.
    /// </summary>
    private Rect Place(int index, Size size)
    {
        var side = SideWidth(size.Width);
        var gap = ColumnSpacing;

        if (side > 0 && SideShare.Stacks(size.Width, side, gap, MinimumMainWidth))
        {
            var unbounded = double.IsInfinity(size.Height);
            var below = unbounded ? double.PositiveInfinity : Math.Max(0, (size.Height - gap) * Share);
            var above = unbounded ? double.PositiveInfinity : Math.Max(0, size.Height - gap - below);

            return index == 0
                ? new Rect(0, 0, size.Width, above)
                : new Rect(0, unbounded ? 0 : above + gap, size.Width, below);
        }

        var main = Math.Max(0, size.Width - side - (side > 0 ? gap : 0));

        return index == 0
            ? new Rect(0, 0, main, size.Height)
            : new Rect(size.Width - side, 0, side, size.Height);
    }

    /// <summary>The card's width, or none while no card is shown.</summary>
    private double SideWidth(double available)
    {
        for (var i = 1; i < Children.Count; i++)
        {
            if (Children[i].Visibility == Visibility.Visible)
            {
                return SideShare.Width(available, Share, MinimumSideWidth, MaximumSideWidth);
            }
        }

        return 0;
    }

    private static void OnLayoutChanged(DependencyObject panel, DependencyPropertyChangedEventArgs args) =>
        ((SideColumn)panel).InvalidateMeasure();
}
