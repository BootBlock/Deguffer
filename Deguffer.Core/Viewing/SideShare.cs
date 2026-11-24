namespace Deguffer.Core.Viewing;

/// <summary>
/// The width of a card beside a picture: a share of the region, held between the narrowest its
/// content reads at and the widest it needs, so the picture keeps most of a narrow window and the
/// card does not sprawl across a wide one.
/// </summary>
public static class SideShare
{
    /// <summary>
    /// The card's width in a region <paramref name="available"/> wide: <paramref name="share"/> of it,
    /// no narrower than <paramref name="minimum"/> and no wider than <paramref name="maximum"/>.
    /// </summary>
    public static double Width(double available, double share, double minimum, double maximum) =>
        double.IsInfinity(available) ? maximum : Math.Clamp(available * share, minimum, Math.Max(minimum, maximum));

    /// <summary>
    /// Whether the card goes under the picture rather than beside it: where beside it, the picture
    /// would be narrower than <paramref name="mainMinimum"/>, and a list there would have no room for
    /// its names and figures.
    /// </summary>
    public static bool Stacks(double available, double side, double gap, double mainMinimum) =>
        available - side - gap < mainMinimum;
}
