namespace Deguffer.Core.Viewing;

/// <summary>
/// Where a row of controls that wraps, such as a row of check boxes or a toolbar, starts a new line.
/// A control goes to the next line only where it would cross the right edge, and never to a line of
/// its own when it is the first on its line: a control wider than the row has nowhere better to go.
/// </summary>
public static class Flow
{
    /// <summary>
    /// How far, in effective pixels, a line may overrun the row before its last control moves down.
    /// A row is often arranged at exactly the width it asked for, and at a fractional display scale
    /// layout rounding can hand it back a fraction of a pixel less than the sum of its controls:
    /// without this, the last control of a row that fits would drop to a line nobody made room for.
    /// </summary>
    public const double RoundingSlack = 1;

    /// <summary>
    /// Whether a control <paramref name="width"/> wide starts a new line, after a line already
    /// <paramref name="line"/> wide in a row <paramref name="available"/> wide.
    /// </summary>
    public static bool Breaks(double line, double width, double gap, double available) =>
        line > 0 && width > 0 && line + gap + width > available + RoundingSlack;
}
