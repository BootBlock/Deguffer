namespace Deguffer.Core.Viewing;

/// <summary>
/// Where a list row's parts go when the row is narrower than they are: the name, a secondary text
/// that may trim, and the chips and figures. The name never gives way, because it is what says which
/// row this is, so when it and the chips cannot share a line the chips move under it.
///
/// <para>The secondary part counts in that decision only at the narrowest it may be: nothing for a
/// sentence the row repeats elsewhere in full, which trims, and its whole width for a button, which
/// must not. On one line it has what lies between the name and the chips. Once the chips have moved,
/// it has what lies beside whichever of the two it goes with (<see cref="WrappedMiddle"/>).</para>
///
/// <para>A part with no width is not on screen, and takes no gap beside it.</para>
/// </summary>
public static class RowReflow
{
    /// <summary>
    /// Whether the chips go to a line of their own under the name, in a row
    /// <paramref name="available"/> pixels wide, with a middle part that may be no narrower than
    /// <paramref name="middle"/>. An unbounded row never wraps.
    /// </summary>
    public static bool Wraps(double lead, double middle, double tail, double gap, double available) =>
        Beside(Beside(lead, middle, gap), tail, gap) > available;

    /// <summary>The width the secondary part has, on whichever line <paramref name="middle"/> puts it.</summary>
    public static double SlackWidth(bool wraps, WrappedMiddle middle, double lead, double tail, double gap, double available)
    {
        if (double.IsInfinity(available))
        {
            return available;
        }

        var name = lead + Gap(lead, gap);
        var chips = tail + Gap(tail, gap);
        var taken = !wraps ? name + chips
            : middle == WrappedMiddle.WithTail ? chips
            : name;

        return Math.Max(0, available - taken);
    }

    /// <summary>The width of two parts side by side.</summary>
    private static double Beside(double first, double second, double gap) => first + second + Gap(first, gap, second);

    private static double Gap(double part, double gap) => part > 0 ? gap : 0;

    private static double Gap(double first, double gap, double second) => first > 0 && second > 0 ? gap : 0;
}
