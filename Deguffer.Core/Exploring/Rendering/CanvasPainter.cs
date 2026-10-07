namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Paints one drawing into a BGRA buffer, a region at a time.
///
/// <para>A region rather than the whole canvas, so a redraw can put each part of the picture on
/// screen as it is finished and the threads painting it can take the parts in the order the reader
/// will look at them (see <see cref="PaintOrder"/>). Everything that is the same for every region,
/// such as the colour of each shape, is worked out once when the painter is made, because a canvas
/// is a hundred regions and each would otherwise work it out again (G4).</para>
///
/// <para>A region is painted on the calling thread. Regions never overlap, so any number of threads
/// can paint different regions of the one buffer at once.</para>
/// </summary>
public abstract class CanvasPainter
{
    private readonly TileColour _background;

    protected CanvasPainter(int width, int height, TileColour background)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        _background = background;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Paint <paramref name="region"/> of the canvas into <paramref name="pixels"/>, a BGRA buffer
    /// of the canvas's size, on the ground first and then every shape that shows there.
    ///
    /// <para>The buffer belongs to the caller and the region is overwritten in full, so a view that
    /// repaints keeps one buffer and hands it back (G5). At 3840 by 2160 it is 33 MB, on the
    /// large-object heap, which is not compacted by default.</para>
    /// </summary>
    public void Paint(byte[] pixels, CanvasRegion region)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        if (pixels.Length < PixelBuffer.LengthFor(Width, Height))
        {
            throw new ArgumentException(
                $"A {Width}x{Height} canvas needs {PixelBuffer.LengthFor(Width, Height)} bytes, not {pixels.Length}.",
                nameof(pixels));
        }

        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
            || region.Right > Width || region.Bottom > Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(region), region, $"The region is not inside the {Width}x{Height} canvas.");
        }

        PixelBuffer.Fill(pixels, Width, region, _background);
        Draw(pixels, region);
    }

    /// <summary>
    /// Draw every shape that shows in <paramref name="region"/> over the ground already in it.
    /// Called only with a region inside the canvas, and a buffer large enough for it.
    /// </summary>
    protected abstract void Draw(byte[] pixels, CanvasRegion region);
}
