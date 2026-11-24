using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Controls;

/// <summary>
/// A page laid out at the width a row can be read across, with only what fills the window allowed
/// past it. Set <c>Limit</c> on the page's root <see cref="Grid"/>, and <c>Fills</c> on each child
/// that takes the whole width: the page's header, and a map while it draws a picture.
///
/// <para>Past a list's reading width, a row's name and its figures drift so far apart that the eye
/// loses the line between them, and the controls that go with a row end up a screen away from it.
/// A picture is read as a whole, so a wider window gives it more to show.</para>
///
/// <para>The grid is given two columns: the reading column, and the rest of the window. A star
/// column with a <c>MaxWidth</c> cannot do it with the built-in columns alone, because two star
/// columns share the width between them, which would halve the reading column on any window
/// narrower than twice the limit; and a child aligned left inside a capped cell shrinks to its own
/// content. So the first column is a star while the page is narrower than the limit, and the limit
/// in pixels once it is wider, with the second column taking the rest. The columns change only as
/// the page crosses the limit, so a resize costs nothing between those moments.</para>
/// </summary>
public static class ReadingColumn
{
    public static readonly DependencyProperty LimitProperty = DependencyProperty.RegisterAttached(
        "Limit", typeof(double), typeof(ReadingColumn), new PropertyMetadata(double.PositiveInfinity, OnLimitChanged));

    public static readonly DependencyProperty FillsProperty = DependencyProperty.RegisterAttached(
        "Fills", typeof(bool), typeof(ReadingColumn), new PropertyMetadata(false, OnFillsChanged));

    private const int Columns = 2;

    private static readonly GridLength Rest = new(1, GridUnitType.Star);

    private static readonly GridLength Nothing = new(0);

    /// <summary>The widest the reading column grows, in effective pixels inside the grid's padding.</summary>
    public static double GetLimit(Grid grid) => (double)grid.GetValue(LimitProperty);

    public static void SetLimit(Grid grid, double value) => grid.SetValue(LimitProperty, value);

    /// <summary>Whether the element takes the whole width of its page rather than the reading column.</summary>
    public static bool GetFills(FrameworkElement element) => (bool)element.GetValue(FillsProperty);

    public static void SetFills(FrameworkElement element, bool value) => element.SetValue(FillsProperty, value);

    private static void OnLimitChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        // Set once from markup on a page's root, before it is first laid out, which is the only use
        // there is. The columns are this layout's own, so the page declares none of its own.
        var grid = (Grid)element;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Rest });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Nothing });
        grid.SizeChanged += OnSizeChanged;
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var grid = (Grid)sender;
        var limit = GetLimit(grid);
        var capped = args.NewSize.Width - grid.Padding.Left - grid.Padding.Right > limit;
        var reading = grid.ColumnDefinitions[0];

        if (capped == reading.Width.IsAbsolute)
        {
            return;
        }

        reading.Width = capped ? new GridLength(limit) : Rest;
        grid.ColumnDefinitions[1].Width = capped ? Rest : Nothing;
    }

    private static void OnFillsChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) =>
        Grid.SetColumnSpan((FrameworkElement)element, (bool)args.NewValue ? Columns : 1);
}
