using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// The edges of a card drawn as a run of list rows, for markup to bind a heading's and a row's
/// border to. A list that virtualises realises each row on its own, so a card around rows it holds
/// would be built whole; drawn in pieces instead, the heading takes the card's top edge and corners,
/// each row its sides, and the last row its bottom edge and corners.
///
/// <para>The corner is the app's <c>CardCorner</c> and the padding its <c>CardRowPadding</c> and
/// <c>ControlGap</c>, read once, so the pieces read as one card beside the page's other cards and
/// stay on the spacing scale when it changes.</para>
/// </summary>
public static class CardRows
{
    private static readonly double Corner = ((CornerRadius)Application.Current.Resources["CardCorner"]).TopLeft;

    /// <summary>The card's padding at each side, above the heading and under the last row.</summary>
    private static readonly Thickness Padding = (Thickness)Application.Current.Resources["CardRowPadding"];

    /// <summary>The heading's padding under its divider, before the first row.</summary>
    private static readonly double Neck = (double)Application.Current.Resources["ControlGap"];

    /// <summary>The heading's border: the top edge and both sides.</summary>
    public static Thickness HeadingSides => new(1, 1, 1, 0);

    /// <summary>The heading's corners: the card's top two.</summary>
    public static CornerRadius HeadingCorners => new(Corner, Corner, 0, 0);

    /// <summary>The heading's padding: the card's inset at each side, the card's padding above and its neck below.</summary>
    public static Thickness HeadingInside => new(Padding.Left, Padding.Top, Padding.Right, Neck);

    /// <summary>A row's border: both sides, and the bottom edge for the last row.</summary>
    public static Thickness Sides(bool last) => new(1, 0, 1, last ? 1 : 0);

    /// <summary>A row's corners: square, except the last row's bottom two.</summary>
    public static CornerRadius Corners(bool last) => last ? new(0, 0, Corner, Corner) : default;

    /// <summary>A row's padding: the card's inset at each side, and the card's padding under the last row.</summary>
    public static Thickness Inside(bool last) => new(Padding.Left, 0, Padding.Right, last ? Padding.Bottom : 0);
}
