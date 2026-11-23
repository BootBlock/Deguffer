namespace Deguffer.Core.Viewing;

/// <summary>
/// Where a row of controls that wraps, such as a row of check boxes or a toolbar, starts a new line.
/// A control goes to the next line only where it would cross the right edge, and never to a line of
/// its own when it is the first on its line: a control wider than the row has nowhere better to go.
/// </summary>
public static class Flow
{
    /// <summary>
    /// Whether a control <paramref name="width"/> wide starts a new line, after a line already
    /// <paramref name="line"/> wide in a row <paramref name="available"/> wide.
    /// </summary>
    public static bool Breaks(double line, double width, double gap, double available) =>
        line > 0 && width > 0 && line + gap + width > available;
}
