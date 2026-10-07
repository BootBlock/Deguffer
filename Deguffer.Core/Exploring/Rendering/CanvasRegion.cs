namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// A rectangle of a canvas, in whole device pixels: the unit a drawing is painted and put on screen
/// in. See <see cref="PaintOrder"/>.
/// </summary>
public readonly record struct CanvasRegion(int X, int Y, int Width, int Height)
{
    /// <summary>The column after the last one in the region.</summary>
    public int Right => X + Width;

    /// <summary>The row after the last one in the region.</summary>
    public int Bottom => Y + Height;

    /// <summary>Whether the canvas point <paramref name="x"/>, <paramref name="y"/> is in the region.</summary>
    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;
}
