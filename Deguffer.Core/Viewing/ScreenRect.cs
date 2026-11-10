namespace Deguffer.Core.Viewing;

/// <summary>
/// A rectangle on the desktop in physical pixels, as Windows reports one to a process aware of each
/// monitor's DPI: <see cref="Left"/> and <see cref="Top"/> inside it, <see cref="Right"/> and
/// <see cref="Bottom"/> just outside.
/// </summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Whether it holds no pixel at all.</summary>
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    /// <summary>The pixels both rectangles hold, which is empty where they do not meet.</summary>
    public ScreenRect Intersect(ScreenRect other) =>
        new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
}
