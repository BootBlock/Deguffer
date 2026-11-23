namespace Deguffer.Core.Viewing;

/// <summary>
/// Where a list row's parts go when the row is narrower than they are: the name, a secondary text
/// that may trim, and the chips and figures. The name never gives way, because it is what says which
/// row this is, so when it and the chips cannot share a line the chips move under it.
///
/// <para>The secondary part is left out of that decision: it gives way rather than move anything,
/// as a sentence the row repeats elsewhere in full, or a control that fits beside a short name. It
/// stays on the name's line either way, and has what lies between the name and the chips, or beside
/// the name once the chips have moved under it.</para>
///
/// <para>A part with no width is not on screen, and takes no gap beside it.</para>
/// </summary>
public static class RowReflow
{
    /// <summary>
    /// Whether the chips go to a line of their own under the name, in a row
    /// <paramref name="available"/> pixels wide. An unbounded row never wraps.
    /// </summary>
    public static bool Wraps(double lead, double tail, double gap, double available) =>
        Beside(lead, tail, gap) > available;

    /// <summary>The width the secondary part has beside the name.</summary>
    public static double SlackWidth(bool wraps, double lead, double tail, double gap, double available)
    {
        if (double.IsInfinity(available))
        {
            return available;
        }

        var name = lead + Gap(lead, gap);
        var taken = wraps ? name : name + tail + Gap(tail, gap);

        return Math.Max(0, available - taken);
    }

    /// <summary>The width of two parts side by side.</summary>
    private static double Beside(double first, double second, double gap) => first + second + Gap(first, gap, second);

    private static double Gap(double part, double gap) => part > 0 ? gap : 0;

    private static double Gap(double first, double gap, double second) => first > 0 && second > 0 ? gap : 0;
}
