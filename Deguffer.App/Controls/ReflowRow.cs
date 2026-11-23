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
/// wrap, and the middle one fills the space beside the name.</para>
///
/// <para>A <c>Grid</c> cannot do this: a column cannot move to a row of its own, and a visual state
/// set from the window's width would wrap rows that fit and leave rows that do not, because each
/// row's name is a different length.</para>
/// </summary>
public sealed partial class ReflowRow : Panel
{
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(ReflowRow), new PropertyMetadata(0.0, OnSpacingChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(ReflowRow), new PropertyMetadata(0.0, OnSpacingChanged));

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
        _wraps = RowReflow.Wraps(lead.DesiredSize.Width, tail.DesiredSize.Width, gap, width);

        var slackWidth = RowReflow.SlackWidth(_wraps, lead.DesiredSize.Width, tail.DesiredSize.Width, gap, width);
        slack?.Measure(new Size(slackWidth, availableSize.Height));

        var slackSize = slack?.DesiredSize ?? default;

        if (_wraps)
        {
            // The name on a line of its own trims only where it is wider than the whole row.
            lead.Measure(availableSize);

            return new Size(
                Math.Min(width, Math.Max(lead.DesiredSize.Width + slackSize.Width + Gap(slack, gap), tail.DesiredSize.Width)),
                FirstLine(lead, slack) + RowSpacing + tail.DesiredSize.Height);
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
        var tailWidth = tail.DesiredSize.Width;

        if (_wraps)
        {
            var first = FirstLine(lead, slack);
            var top = first + RowSpacing;
            var second = Math.Max(0, finalSize.Height - top);

            lead.Arrange(Centred(0, 0, leadWidth, first, lead));
            tail.Arrange(Centred(Math.Max(0, finalSize.Width - tailWidth), top, tailWidth, second, tail));

            if (slack is not null)
            {
                var left = leadWidth + Gap(lead, gap);
                slack.Arrange(Centred(left, 0, Math.Max(0, finalSize.Width - left), first, slack));
            }

            return finalSize;
        }

        var height = finalSize.Height;
        var tailLeft = Math.Max(0, finalSize.Width - tailWidth);

        lead.Arrange(Centred(0, 0, leadWidth, height, lead));
        tail.Arrange(Centred(tailLeft, 0, tailWidth, height, tail));

        if (slack is not null)
        {
            var left = leadWidth + Gap(lead, gap);
            slack.Arrange(Centred(left, 0, Math.Max(0, tailLeft - Gap(tail, gap) - left), height, slack));
        }

        return finalSize;
    }

    /// <summary>A slot <paramref name="height"/> tall with <paramref name="child"/> in the middle of it, as a Grid cell centres it.</summary>
    private static Rect Centred(double left, double top, double width, double height, UIElement child)
    {
        var own = Math.Min(child.DesiredSize.Height, height);

        return new Rect(left, top + ((height - own) / 2), width, own);
    }

    private static double FirstLine(UIElement lead, UIElement? slack) =>
        Math.Max(lead.DesiredSize.Height, slack?.DesiredSize.Height ?? 0);

    private static double Gap(UIElement? part, double gap) => part is { DesiredSize.Width: > 0 } ? gap : 0;

    private static int Gaps(UIElement lead, UIElement? slack, UIElement tail)
    {
        var shown = (lead.DesiredSize.Width > 0 ? 1 : 0)
            + (slack is { DesiredSize.Width: > 0 } ? 1 : 0)
            + (tail.DesiredSize.Width > 0 ? 1 : 0);

        return Math.Max(0, shown - 1);
    }

    private static void OnSpacingChanged(DependencyObject row, DependencyPropertyChangedEventArgs args) =>
        ((ReflowRow)row).InvalidateMeasure();
}
