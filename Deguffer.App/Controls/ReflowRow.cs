using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Deguffer.App.Controls;

/// <summary>
/// A row's name line that keeps the name whole on a narrow window by moving the chips and figures
/// under it. <see cref="RowReflow"/> decides when, and what the secondary text has.
///
/// <para>Two or three children, in reading order: the name first, the chips and figures last, and
/// between them, where there is one, a part that gives way, such as a status that trims. On one line
/// the last child is right-aligned and the middle one fills the space between. On two, the last
/// child is right-aligned under the name, so the chips stay in one column with the rows that did not
/// wrap, and the middle one fills the space beside the name or beside the last child, as
/// <see cref="Middle"/> says.</para>
///
/// <para>Each child is given the whole height of its line and places itself in it by its own
/// <c>VerticalAlignment</c>, as it would in a Grid cell: a button beside a box with a header above it
/// asks to sit at the bottom, level with the box.</para>
///
/// <para>A <c>Grid</c> cannot do this: a column cannot move to a row of its own, and a visual state
/// set from the window's width would wrap rows that fit and leave rows that do not, because each
/// row's name is a different length.</para>
/// </summary>
public sealed partial class ReflowRow : Panel
{
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(ReflowRow), new PropertyMetadata(0.0, OnLayoutChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(ReflowRow), new PropertyMetadata(0.0, OnLayoutChanged));

    public static readonly DependencyProperty MiddleProperty = DependencyProperty.Register(
        nameof(Middle), typeof(WrappedMiddle), typeof(ReflowRow), new PropertyMetadata(WrappedMiddle.WithLead, OnLayoutChanged));

    public static readonly DependencyProperty MiddleStaysWholeProperty = DependencyProperty.Register(
        nameof(MiddleStaysWhole), typeof(bool), typeof(ReflowRow), new PropertyMetadata(false, OnLayoutChanged));

    private bool _wraps;

    /// <summary>The gap between parts on one line.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>The gap between the name's line and the line under it.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Which line the middle child stays on once the row wraps.</summary>
    public WrappedMiddle Middle
    {
        get => (WrappedMiddle)GetValue(MiddleProperty);
        set => SetValue(MiddleProperty, value);
    }

    /// <summary>
    /// Whether the middle child is drawn whole, as a button must be, so the row wraps rather than
    /// squeeze it. Otherwise it may shrink to its <c>MinWidth</c>, as a status that trims may.
    /// </summary>
    public bool MiddleStaysWhole
    {
        get => (bool)GetValue(MiddleStaysWholeProperty);
        set => SetValue(MiddleStaysWholeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lead = Children[0];
        var tail = Children[Children.Count - 1];
        var slack = Children.Count > 2 ? Children[1] : null;
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);

        lead.Measure(unbounded);
        tail.Measure(unbounded);

        var gap = ColumnSpacing;
        var width = availableSize.Width;
        var middle = slack is FrameworkElement part ? Narrowest(part, unbounded) : 0;
        _wraps = RowReflow.Wraps(lead.DesiredSize.Width, middle, tail.DesiredSize.Width, gap, width);

        var withTail = Middle == WrappedMiddle.WithTail;
        var slackWidth = RowReflow.SlackWidth(_wraps, Middle, lead.DesiredSize.Width, tail.DesiredSize.Width, gap, width);
        slack?.Measure(new Size(slackWidth, availableSize.Height));

        var slackSize = slack?.DesiredSize ?? default;

        if (_wraps)
        {
            // On a line of its own, each part is held to the row: the name trims only where it is
            // wider than the whole row, and a last part that wraps its own controls, such as a
            // FlowRow, wraps them rather than run off the edge.
            lead.Measure(availableSize);
            tail.Measure(availableSize);

            var withSlack = slackSize.Width + Gap(slack, gap);
            var first = withTail ? lead.DesiredSize.Width : lead.DesiredSize.Width + withSlack;
            var second = withTail ? tail.DesiredSize.Width + withSlack : tail.DesiredSize.Width;

            return new Size(
                Math.Min(width, Math.Max(first, second)),
                Line(lead, withTail ? null : slack) + RowSpacing + Line(tail, withTail ? slack : null));
        }

        var line = Math.Max(lead.DesiredSize.Height, Math.Max(slackSize.Height, tail.DesiredSize.Height));
        var used = lead.DesiredSize.Width + tail.DesiredSize.Width + slackSize.Width + (gap * Gaps(lead, slack, tail));

        return new Size(double.IsInfinity(width) ? used : Math.Min(width, used), line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var lead = Children[0];
        var tail = Children[Children.Count - 1];
        var slack = Children.Count > 2 ? Children[1] : null;
        var gap = ColumnSpacing;
        var leadWidth = Math.Min(lead.DesiredSize.Width, finalSize.Width);
        var tailWidth = Math.Min(tail.DesiredSize.Width, finalSize.Width);

        if (_wraps)
        {
            var withTail = Middle == WrappedMiddle.WithTail;
            var first = Line(lead, withTail ? null : slack);
            var top = first + RowSpacing;
            var second = Math.Max(0, finalSize.Height - top);
            var tailLeftWrapped = Math.Max(0, finalSize.Width - tailWidth);

            lead.Arrange(new Rect(0, 0, leadWidth, first));
            tail.Arrange(new Rect(tailLeftWrapped, top, tailWidth, second));

            if (slack is not null && withTail)
            {
                slack.Arrange(new Rect(0, top, Math.Max(0, tailLeftWrapped - Gap(tail, gap)), second));
            }
            else if (slack is not null)
            {
                var left = leadWidth + Gap(lead, gap);
                slack.Arrange(new Rect(left, 0, Math.Max(0, finalSize.Width - left), first));
            }

            return finalSize;
        }

        var height = finalSize.Height;
        var tailLeft = Math.Max(0, finalSize.Width - tailWidth);

        lead.Arrange(new Rect(0, 0, leadWidth, height));
        tail.Arrange(new Rect(tailLeft, 0, tailWidth, height));

        if (slack is not null)
        {
            var left = leadWidth + Gap(lead, gap);
            slack.Arrange(new Rect(left, 0, Math.Max(0, tailLeft - Gap(tail, gap) - left), height));
        }

        return finalSize;
    }

    /// <summary>The narrowest <paramref name="part"/> may be drawn, measuring it whole where it has to stay whole.</summary>
    private double Narrowest(FrameworkElement part, Size unbounded)
    {
        if (!MiddleStaysWhole)
        {
            return part.Visibility == Visibility.Visible ? part.MinWidth : 0;
        }

        part.Measure(unbounded);

        return part.DesiredSize.Width;
    }

    /// <summary>The height of a wrapped line holding <paramref name="part"/> and, where it is on that line, the middle.</summary>
    private static double Line(UIElement part, UIElement? slack) =>
        Math.Max(part.DesiredSize.Height, slack?.DesiredSize.Height ?? 0);

    private static double Gap(UIElement? part, double gap) => part is { DesiredSize.Width: > 0 } ? gap : 0;

    private static int Gaps(UIElement lead, UIElement? slack, UIElement tail)
    {
        var shown = (lead.DesiredSize.Width > 0 ? 1 : 0)
            + (slack is { DesiredSize.Width: > 0 } ? 1 : 0)
            + (tail.DesiredSize.Width > 0 ? 1 : 0);

        return Math.Max(0, shown - 1);
    }

    private static void OnLayoutChanged(DependencyObject row, DependencyPropertyChangedEventArgs args) =>
        ((ReflowRow)row).InvalidateMeasure();
}
