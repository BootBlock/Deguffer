namespace Deguffer.Core.Viewing;

/// <summary>
/// Whether a region's parts sit side by side in equal columns or stack one above another: side by
/// side only where every column gets at least the width its content was laid out for.
/// </summary>
public static class Columns
{
    /// <summary>
    /// Whether <paramref name="count"/> columns, each at least <paramref name="minimum"/> wide with
    /// <paramref name="gap"/> between them, fit in <paramref name="available"/>.
    /// </summary>
    public static bool SideBySide(int count, double minimum, double gap, double available) =>
        count < 2 || (count * minimum) + ((count - 1) * gap) <= available;

    /// <summary>The width of each of <paramref name="count"/> equal columns side by side.</summary>
    public static double Width(int count, double gap, double available) =>
        Math.Max(0, (available - ((count - 1) * gap)) / count);
}
