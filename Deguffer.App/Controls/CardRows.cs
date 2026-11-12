using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// The edges of a card drawn as a run of list rows, for markup to bind a row's border to. A list
/// that virtualises realises each row on its own, so a card around rows it holds would be built
/// whole; drawn in pieces instead, the heading takes the card's top edge and corners, each row its
/// sides, and the last row its bottom edge and corners.
///
/// <para>The corner matches <c>CardCorner</c>, so the pieces read as one card beside the page's
/// other cards.</para>
/// </summary>
public static class CardRows
{
    private const double Corner = 8;

    private const double Inset = 16;

    /// <summary>The bottom padding of the last row, under its content, before the card's edge.</summary>
    private const double Foot = 10;

    /// <summary>A row's border: both sides, and the bottom edge for the last row.</summary>
    public static Thickness Sides(bool last) => new(1, 0, 1, last ? 1 : 0);

    /// <summary>A row's corners: square, except the last row's bottom two.</summary>
    public static CornerRadius Corners(bool last) => last ? new(0, 0, Corner, Corner) : default;

    /// <summary>A row's padding: the card's inset at each side, and its foot under the last row.</summary>
    public static Thickness Inside(bool last) => new(Inset, 0, Inset, last ? Foot : 0);
}
